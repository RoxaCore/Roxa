<?php
// Release checklist + endpoint health probes, shared by checks.php (the full
// page) and index.php (the one-line summary on the dashboard).

/**
 * Static checks — files and flags only, no network, safe to run on every
 * page load. Each item: ok (bool), label, hint (what to do when red).
 */
function checklist_items(): array
{
    $ver = jread(DATA_DIR . '/version.json', ['latest' => null, 'active' => false, 'file' => null, 'url' => null]);
    $latest = (string)($ver['latest'] ?? '');
    $out = [];

    // 1 — a version is actually being served to clients.
    $out[] = [
        'ok' => $latest !== '' && !empty($ver['active']),
        'label' => 'نسخهٔ منتشرشده و فعال',
        'hint' => $latest === ''
            ? 'هنوز نسخه‌ای در «نسخه و آپدیت» ثبت نشده است.'
            : (empty($ver['active'])
                ? 'نسخهٔ ' . $latest . ' ذخیره شده ولی تیک «فعال» ندارد؛ کاربران آن را نمی‌بینند.'
                : 'نسخهٔ ' . $latest . ' برای کاربران ارسال می‌شود.'),
    ];

    // 2 — the update package itself is reachable.
    $file = (string)($ver['file'] ?? '');
    $url = (string)($ver['url'] ?? '');
    if ($url !== '') {
        $out[] = [
            'ok' => true,
            'label' => 'فایل بروزرسانی (لینک مستقیم)',
            'hint' => $url,
        ];
    } else {
        $hasFile = $file !== '' && is_file(DL_DIR . '/' . $file);
        $out[] = [
            'ok' => $hasFile,
            'label' => 'فایل بروزرسانی در پوشهٔ dl',
            'hint' => $hasFile
                ? $file . ' (' . number_format((int)@filesize(DL_DIR . '/' . $file) / 1048576, 1) . ' MB)'
                : ($file === ''
                    ? 'چیزی منتشر نشده است؛ در «نسخه و آپدیت» zip را آپلود کنید.'
                    : $file . ' در dl/ پیدا نشد — آپلود ناتمام مانده یا فایل پاک شده است.'),
        ];
    }

    // 3 — the online installer the landing page links to.
    $exe = 'Roxa-Setup-' . $latest . '-online.exe';
    $hasExe = $latest !== '' && is_file(DL_DIR . '/' . $exe);
    $out[] = [
        'ok' => $hasExe,
        'label' => 'نصب‌کنندهٔ آنلاین برای لندینگ',
        'hint' => $hasExe
            ? $exe
            : 'لینک‌های سایت به ' . $exe . ' می‌زنند؛ تا آپلود آن در public_html/up/dl/ دکمهٔ دانلود 404 می‌دهد.',
    ];

    // 4 — data must not sit under the document root (the .htaccess shield is
    // the only web-facing protection there, and nginx ignores it).
    $out[] = [
        'ok' => !data_dir_inside_webroot(),
        'label' => 'پوشهٔ داده بیرون از سند-ریشه',
        'hint' => data_dir_inside_webroot()
            ? 'با php tools/migrate-data.php --apply به مسیر امن منتقل کنید.'
            : DATA_DIR,
    ];

    // 5 — a recent backup exists (a JSON-only store is fast to lose).
    $bk = jread(DATA_DIR . '/backup.json', ['at' => null]);
    $bkAge = $bk['at'] ? time() - (strtotime((string)$bk['at']) ?: 0) : PHP_INT_MAX;
    $out[] = [
        'ok' => $bkAge < 7 * 86400,
        'label' => 'پشتیبانِ تازهٔ data (کمتر از ۷ روز)',
        'hint' => $bk['at']
            ? 'آخرین پشتیبان: ' . date('Y-m-d H:i', strtotime((string)$bk['at']) ?: 0)
            : 'هنوز پشتیبانی گرفته نشده؛ از صفحهٔ «پشتیبان‌گیری» با یک کلیک بگیرید.',
    ];

    // 6 — writable stores: counters and stats silently stop otherwise.
    $out[] = [
        'ok' => is_writable(DATA_DIR),
        'label' => 'پوشهٔ داده قابل نوشتن',
        'hint' => is_writable(DATA_DIR) ? DATA_DIR : 'دسترسی نوشتن ندارد؛ آمار و لاگ‌ها به‌روز نمی‌شوند.',
    ];

    return $out;
}

/**
 * One HTTP probe: status code + latency, no body kept. follow_location is off
 * so a 302 from api/dl.php is observed as-is. Returns ok=false when the
 * server could not be reached at all.
 */
function http_probe(string $url, int $expect): array
{
    $ctx = stream_context_create(['http' => [
        'method' => 'GET',
        'timeout' => 5,
        'ignore_errors' => true,
        'follow_location' => 0,
        'header' => "User-Agent: Roxa-Panel-Health\r\nAccept: */*\r\n",
    ]]);
    $t0 = microtime(true);
    $body = @file_get_contents($url, false, $ctx);
    $ms = (int)round((microtime(true) - $t0) * 1000);
    $status = 0;
    foreach (($http_response_header ?? []) as $h) {
        if (preg_match('#^HTTP/\S+\s+(\d{3})#', $h, $m)) {
            $status = (int)$m[1]; // last line wins
        }
    }
    unset($body);
    return [
        'url' => $url,
        'expect' => $expect,
        'got' => $status,
        'ms' => $ms,
        'ok' => $status === $expect,
    ];
}

/**
 * The four endpoints the site and the app depend on. dl.php is probed with an
 * invalid name on purpose: a 404 proves the router and the script are alive
 * without counting a fake download.
 */
function health_probes(): array
{
    return [
        http_probe(BASE_URL . '/', 200),
        http_probe(BASE_URL . '/api/check.php?v=0.0.1', 200),
        http_probe(BASE_URL . '/api/announcements.php', 200),
        http_probe(BASE_URL . '/api/dl.php?f=__probe__.zip', 404),
    ];
}
