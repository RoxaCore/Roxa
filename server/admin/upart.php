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

// Release the session lock right after the CSRF check. act=finish assembles
// the whole zip (seconds on a 13MB file) and every chunk request used to hold
// the same lock, so each other panel page opened during a publish queued
// behind this request — the panel felt frozen exactly while uploading.
// Nothing below reads $_SESSION again (audit_log only reads the in-memory
// copy, which session_write_close keeps).
session_write_close();

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

    // Where this request continues the chunk. A body cut mid-transfer never
    // reaches PHP, so the stored chunk still ends where the client last heard
    // back from us — the retry resumes from that byte instead of starting over.
    // A gap (offset beyond what is stored) would splice the file, so it is
    // rejected rather than guessed at.
    $offset = (int)($_POST['offset'] ?? 0);

    $dir = upart_dir($token);
    if (!is_dir($dir) && !@mkdir($dir, 0700, true)) {
        upart_fail('ذخیرهٔ قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }
    @file_put_contents($dir . '/.total', (string)$total);

    $path = $dir . '/' . sprintf('%06d', $index) . '.part';
    $cur = is_file($path) ? (int)@filesize($path) : 0;
    if ($offset < 0 || $offset > $cur) {
        upart_fail('آفست قطعه نامعتبر است.');
    }
    if ($offset < $cur && !@truncate($path, $offset)) {
        upart_fail('بازنویسی قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }

    // c+b: create when missing, never truncate — the piece is appended at
    // $offset. is_uploaded_file() keeps the check move_uploaded_file() gave us.
    $out = @fopen($path, 'c+b');
    if ($out === false) {
        upart_fail('ذخیرهٔ قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }
    if ($offset > 0) {
        @fseek($out, $offset);
    }
    $in = @fopen((string)$f['tmp_name'], 'rb');
    $ok = $in !== false
        && is_uploaded_file((string)$f['tmp_name'])
        && @stream_copy_to_stream($in, $out) !== false;
    if ($in !== false) {
        fclose($in);
    }
    fclose($out);
    if (!$ok) {
        @unlink($path);
        upart_fail('ذخیرهٔ قطعه ممکن نشد — دسترسی پوشه داده را بررسی کنید.', 500);
    }
    if ((int)@filesize($path) > UPART_CHUNK_MAX) {
        @unlink($path);
        upart_fail('حجم قطعه از حد مجاز بیشتر است.');
    }

    // Keep the dir mtime fresh so the sweep never eats an upload in progress.
    @touch($dir);
    upart_json(true, '', ['index' => $index, 'offset' => $offset, 'size' => (int)@filesize($path)]);
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

    // The client also sends the original size and the logical chunk size, so
    // every stored chunk can be checked for its exact expected length. Without
    // this a piece lost mid-chunk would still assemble into a file that only
    // fails much later, inside the client's hash check.
    $fs = (int)($_POST['filesize'] ?? 0);
    $ck = (int)($_POST['chunk'] ?? 0);
    if ($fs > 0 && $ck > 0) {
        $bad = ($size !== $fs);
        if (!$bad) {
            for ($i = 0; $i < $total; $i++) {
                $expect = min($ck, $fs - $i * $ck);
                $actual = (int)@filesize($dir . '/' . sprintf('%06d', $i) . '.part');
                if ($expect <= 0 || $actual !== $expect) {
                    $bad = true;
                    break;
                }
            }
        }
        if ($bad) {
            upart_rm($dir);
            upart_fail('یکی از قطعه‌ها ناقص است؛ آپلود را دوباره انجام دهید.');
        }
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
