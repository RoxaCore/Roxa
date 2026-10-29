<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$meta = jread(DATA_DIR . '/backup.json', ['at' => null, 'files' => 0, 'bytes' => 0, 'fmt' => '']);

// The download itself: build the archive in the system temp dir (never inside
// DATA_DIR, which would archive itself), stream it, then delete the temp copy.
if (isset($_GET['dl'])) {
    $base = sys_get_temp_dir() . '/roxa-backup-' . date('Ymd-His');
    try {
        $b = backup_build($base);
    } catch (Throwable $e) {
        flash('ساخت پشتیبان ناموفق بود: ' . $e->getMessage(), 'err');
        header('Location: backup.php');
        exit;
    }

    jwrite(DATA_DIR . '/backup.json', [
        'at' => date('c'),
        'files' => $b['files'],
        'bytes' => $b['bytes'],
        'fmt' => $b['fmt'],
    ]);
    audit_log('backup', $b['files'] . ' فایل، ' . number_format($b['bytes'] / 1048576, 1) . ' MB (' . $b['fmt'] . ')');

    $name = 'roxa-backup-' . date('Y-m-d') . '.' . $b['fmt'];
    header('Content-Type: application/octet-stream');
    header('Content-Disposition: attachment; filename="' . $name . '"');
    header('Content-Length: ' . $b['bytes']);
    header('Cache-Control: no-store');
    readfile($b['path']);
    @unlink($b['path']);
    exit;
}

page_head('پشتیبان‌گیری', 'bak');

echo '<div class="grid">';
echo '<div class="stat"><div class="k">آخرین پشتیبان</div><div class="v"><small>'
    . ($meta['at'] ? h(date('Y-m-d H:i', strtotime((string)$meta['at']) ?: 0)) : 'هرگز')
    . '</small></div></div>';
echo '<div class="stat"><div class="k">حجم</div><div class="v"><small>'
    . ($meta['at'] ? number_format((int)$meta['bytes'] / 1048576, 1) . ' MB / ' . (int)$meta['files'] . ' فایل' : '—')
    . '</small></div></div>';
echo '<div class="stat"><div class="k">فرمت</div><div class="v"><small>' . h((string)($meta['fmt'] ?: '—')) . '</small></div></div>';
echo '</div>';

echo '<div class="card"><h2>پشتیبان یک‌کلیکی از data</h2>';
echo '<div class="hint">همهٔ داده‌های پنل — <code>auth.json</code>، <code>stats.json</code>، <code>reports.json</code>، '
    . '<code>dlstats.json</code>، <code>daily.json</code>، <code>audit.json</code> و بقیه — در یک فایل فشرده. '
    . 'فایل‌های موقت (قفل‌ها و نشست‌ها) کنار گذاشته می‌شوند.<br>'
    . 'فرمت: با <code>ZipArchive</code> ساخته می‌شود وگرنه <code>tar.gz</code> — هر دو بدون ابزارِ جانبی باز می‌شوند '
    . '(ویندوز ۱۰/۱۱ خودش <code>tar</code> دارد).<br>'
    . '<b>این فایل شامل رمز پنل است</b>؛ جای امن نگهش دارید و هرگز در دسترسِ وب قرار ندهید.</div>';
echo '<a class="btn" href="backup.php?dl=1">دانلود پشتیبان الان</a>';

if ($meta['at']) {
    $age = time() - (strtotime((string)$meta['at']) ?: 0);
    if ($age > 7 * 86400) {
        echo '<div class="msg warn" style="margin-top:14px">آخرین پشتیبان بیش از ۷ روز پیش گرفته شده است.</div>';
    }
}
echo '</div>';

echo '<div class="card"><h2>بازیابی</h2><div class="hint">';
echo 'محتوای آرشیو همان ساختارِ <code>data/</code> است: فایل‌ها را در مسیرِ دادهٔ پنل باز کنید '
    . '(همان چیزی که <code>ROXA_DATA</code> به آن اشاره می‌کند یا پوشهٔ <code>roxa-data</code>) و پنل را ری‌استارت کنید. '
    . 'روی سرورِ لینوکسی: <code>tar -xzf roxa-backup-....tar.gz -C /مسیر/داده</code>، یا zip را در همان مسیر extract کنید.</div></div>';

page_foot();
