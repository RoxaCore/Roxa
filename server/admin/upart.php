<?php
/**
 * Chunked upload endpoint for the version form.
 *
 * Why: the classic single-request upload dies on hosts whose nginx/proxy
 * limit the request body (client_max_body_size, LimitRequestBody, ...) —
 * the connection is reset mid-upload and the panel shows a misleading
 * "connection lost" error. Here the browser sends the zip in 512KB chunks;
 * every request stays far below any per-request limit, so the upload works
 * on shared hosting without touching php.ini or nginx.
 *
 * Protocol (POST, always AJAX/JSON, both steps need auth + CSRF):
 *   act=chunk  token(32 hex) index total part(file)  -> one chunk stored
 *   act=finish token total + the same publish fields as version.php
 *              -> assemble, verify, publish version.json, cleanup
 *
 * Chunks live in DATA_DIR/uparts/<token>/000000.part ... and are removed
 * after a successful finish (or after 2 hours, swept on any later request).
 */
require_once __DIR__ . '/_layout.php';
require_auth();

// Limits: UPART_CHUNK_MAX / UPART_TOTAL_MAX / UPART_CHUNKS_MAX / UPART_STALE_SEC
// are defined in lib.php (shared with the version form).

function upart_json(bool $ok, string $msg, array $extra = []): void
{
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode(array_merge(['ok' => $ok, 'msg' => $msg], $extra), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

function upart_dir(string $token): string
{
    return DATA_DIR . '/uparts/' . $token;
}

/** Recursive delete, confined to DATA_DIR/uparts. */
function upart_rm(string $dir): void
{
    $base = realpath(DATA_DIR . '/uparts');
    $real = realpath($dir);
    if ($base === false || $real === false || !path_is_within($real, $base)) {
        return;
    }
    $it = new RecursiveIteratorIterator(
        new RecursiveDirectoryIterator($real, FilesystemIterator::SKIP_DOTS),
        RecursiveIteratorIterator::CHILD_FIRST
    );
    foreach ($it as $f) {
        $f->isFile() ? @unlink($f->getPathname()) : @rmdir($f->getPathname());
    }
    @rmdir($real);
}

/** Drop unfinished uploads older than UPART_STALE_SEC. */
function upart_sweep(): void
{
    $base = DATA_DIR . '/uparts';
    if (!is_dir($base)) {
        return;
    }
    $now = time();
    foreach ((array)@scandir($base) as $e) {
        if ($e === '.' || $e === '..') {
            continue;
        }
        $d = $base . '/' . $e;
        $mtime = @filemtime($d);
        if (is_dir($d) && $mtime !== false && $now - $mtime > UPART_STALE_SEC) {
            upart_rm($d);
        }
    }
}

function upart_fail(string $msg, int $code = 400): void
{
    http_response_code($code);
    upart_json(false, $msg);
}

if ($_SERVER['REQUEST_METHOD'] !== 'POST') {
    upart_fail('درخواست نامعتبر است.', 405);
}

// Same AJAX CSRF check as version.php — a plain 419 text page would be
// unparseable for the uploader.
sess_start();
$csrf = (string)($_POST['csrf'] ?? '');
if ($csrf === '' || empty($_SESSION['csrf']) || !hash_equals((string)$_SESSION['csrf'], $csrf)) {
    upart_fail('نشست شما منقضی شده است؛ صفحه را تازه‌سازی کنید و دوباره تلاش کنید.', 419);
}

$act = (string)($_POST['act'] ?? '');
$token = strtolower((string)($_POST['token'] ?? ''));
if (!preg_match('/^[0-9a-f]{32}$/', $token)) {
    upart_fail('شناسهٔ آپلود نامعتبر است.');
}
$total = (int)($_POST['total'] ?? 0);
if ($total < 1 || $total > UPART_CHUNKS_MAX) {
    upart_fail('تعداد قطعه‌ها نامعتبر است.');
}

upart_sweep();

if ($act === 'chunk') {
    $index = (int)($_POST['index'] ?? -1);
    if ($index < 0 || $index >= $total) {
        upart_fail('شمارهٔ قطعه نامعتبر است.');
    }
    if (!isset($_FILES['part']) || !is_array($_FILES['part'])) {
        upart_fail('فایل قطعه دریافت نشد.');
    }
    $f = $_FILES['part'];
    if (($f['error'] ?? -1) !== UPLOAD_ERR_OK) {
        upart_fail('خطا در آپلود قطعه (کد ' . (int)$f['error'] . ').');
    }
    if ((int)$f['size'] > UPART_CHUNK_MAX) {
        upart_fail('حجم قطعه از حد مجاز بیشتر است.');
    }
    $dir = upart_dir($token);
    if (!is_dir($dir) && !@mkdir($dir, 0700, true)) {
        upart_fail('ذخیرهٔ قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }
    @file_put_contents($dir . '/.total', (string)$total);
    if (!move_uploaded_file((string)$f['tmp_name'], $dir . '/' . sprintf('%06d', $index) . '.part')) {
        upart_fail('انتقال قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }
    // Keep the dir mtime fresh so the sweep never eats an upload in progress.
    @touch($dir);
    upart_json(true, '', ['index' => $index]);
}

if ($act === 'finish') {
    $latest = ver_safe((string)($_POST['latest'] ?? ''));
    if ($latest === '') {
        upart_fail('شمارهٔ نسخه اجباری است.');
    }

    $dir = upart_dir($token);
    $size = 0;
    for ($i = 0; $i < $total; $i++) {
        $p = $dir . '/' . sprintf('%06d', $i) . '.part';
        if (!is_file($p)) {
            upart_rm($dir);
            upart_fail('قطعهٔ ' . ($i + 1) . ' از ' . $total . ' دریافت نشده است؛ آپلود را از ابتدا انجام دهید.');
        }
        $size += (int)filesize($p);
    }
    if ($size > UPART_TOTAL_MAX || $size <= 0) {
        upart_rm($dir);
        upart_fail('حجم کل فایل نامعتبر است.');
    }

    // Zip magic on the first chunk — catches a truncated/corrupt transfer
    // before anything is published.
    $head = (string)file_get_contents($dir . '/' . sprintf('%06d', 0) . '.part', false, null, 0, 4);
    if (substr($head, 0, 2) !== "PK") {
        upart_rm($dir);
        upart_fail('فایل دریافت‌شده یک zip معتبر نیست؛ آپلود را دوباره انجام دهید.');
    }

    if (!is_dir(DL_DIR)) {
        @mkdir(DL_DIR, 0755, true);
    }
    $destName = 'roxa-' . $latest . '.zip';
    $dest = DL_DIR . '/' . $destName;
    $tmp = DL_DIR . '/.tmp-' . $token . '.zip';
    $out = @fopen($tmp, 'wb');
    if ($out === false) {
        upart_rm($dir);
        upart_fail('ساخت فایل نهایی ممکن نشد — دسترسی پوشه dl را بررسی کنید.', 500);
    }
    for ($i = 0; $i < $total; $i++) {
        $in = @fopen($dir . '/' . sprintf('%06d', $i) . '.part', 'rb');
        if ($in === false) {
            fclose($out);
            @unlink($tmp);
            upart_rm($dir);
            upart_fail('خواندن قطعه ' . ($i + 1) . ' ممکن نشد؛ آپلود را دوباره انجام دهید.', 500);
        }
        while (!feof($in)) {
            $buf = fread($in, 262144);
            if ($buf === false) {
                break;
            }
            if ($buf !== '') {
                fwrite($out, $buf);
            }
        }
        fclose($in);
    }
    fclose($out);

    $finalSize = (int)@filesize($tmp);
    if ($finalSize !== $size) {
        @unlink($tmp);
        upart_rm($dir);
        upart_fail('فایل نهایی کامل ساخته نشد؛ آپلود را دوباره انجام دهید.', 500);
    }
    if (!@rename($tmp, $dest)) {
        @unlink($tmp);
        upart_rm($dir);
        upart_fail('انتقال فایل به پوشه dl ممکن نشد.', 500);
    }
    @chmod($dest, 0644);
    upart_rm($dir);

    $sha = hash_file('sha256', $dest) ?: '';
    $notes = trim((string)($_POST['notes'] ?? ''));
    $force = isset($_POST['force']);
    $active = isset($_POST['active']);
    version_publish($latest, $notes, $force, $active, null, $destName, $sha, $finalSize, country_field(), langs_field());
    upart_json(
        true,
        $active ? 'نسخه ' . $latest . ' منتشر شد.' : 'نسخه ' . $latest . ' ذخیره شد (غیرفعال — برای انتشار تیک فعال را بزنید).',
        ['sha256' => $sha, 'size' => $finalSize]
    );
}

upart_fail('عملیات نامعتبر است.');
