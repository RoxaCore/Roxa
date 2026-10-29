<?php
require_once __DIR__ . '/config.php';

date_default_timezone_set(TZ);

function ini_bytes(string $val): int
{
    $val = strtolower(trim($val));
    if ($val === '' || $val === '-1' || $val === 'off' || $val === '0') {
        return 0;
    }
    $n = (float)$val;
    if (substr($val, -1) === 'g') {
        $n *= 1073741824;
    } elseif (substr($val, -1) === 'm') {
        $n *= 1048576;
    } elseif (substr($val, -1) === 'k') {
        $n *= 1024;
    }
    return (int)$n;
}

function post_size_exceeded(): bool
{
    $max = ini_bytes((string)ini_get('post_max_size'));
    if ($max <= 0) {
        return false;
    }
    $len = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    return $len > $max;
}

// Early check: if POST is larger than post_max_size, PHP empties $_POST and
// csrf_check returns 419/CSRF or a chain of warnings instead of the proper error. We bail out before the session starts.
if (post_size_exceeded()) {
    // If PHP has already printed a Warning, headers_sent() is true and we must no longer call
    // http_response_code/header (otherwise two extra Warnings are produced).
    if (!headers_sent()) {
        http_response_code(413);
        header('Content-Type: text/plain; charset=utf-8');
    }
    $limit = (string)ini_get('post_max_size');
    $got = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    $gotMb = $got > 0 ? number_format($got / 1048576, 1) . ' MB' : 'نامشخص';
    exit(
        'حجم درخواست POST (' . $gotMb . ') از محدودیت سرور (post_max_size = ' . $limit . ') بیشتر است.' . "\n" .
        'برای انتشار نسخه یکی از کارهای زیر را انجام دهید:' . "\n" .
        '  ۱) upload_max_filesize و post_max_size را در php.ini / .user.ini / پنل هاست بالا ببرید،' . "\n" .
        '  ۲) یا در پنل «نسخه و آپدیت» به‌جای آپلود، از حالت «لینک مستقیم» استفاده کنید.' . "\n"
    );
}

function sess_start(): void
{
    if (session_status() === PHP_SESSION_ACTIVE) {
        return;
    }
    $secure = !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off';
    session_name('ROXAPANEL');
    session_set_cookie_params([
        'lifetime' => 0,
        'path' => '/',
        'secure' => $secure,
        'httponly' => true,
        'samesite' => 'Lax',
    ]);
    session_start();
    if (isset($_SESSION['auth_at']) && time() - $_SESSION['auth_at'] > SESSION_LIFETIME) {
        $_SESSION = [];
        session_destroy();
        session_start();
    }
}

/**
 * Reading under a shared lock. Without this, a concurrent reader can see a partial
 * file between truncate and write.
 */
function jread(string $file, array $default): array
{
    if (!is_file($file)) {
        return $default;
    }
    $fp = @fopen($file, 'rb');
    if (!$fp) {
        return $default;
    }
    try {
        flock($fp, LOCK_SH);
        $raw = stream_get_contents($fp);
    } finally {
        flock($fp, LOCK_UN);
        fclose($fp);
    }
    if ($raw === false || $raw === '') {
        return $default;
    }
    $data = json_decode($raw, true);
    return is_array($data) ? $data : $default;
}

/**
 * Atomic write: same-name temp file + rename. A concurrent reader sees either the previous
 * version or the next one — never a partial one.
 */
function jwrite(string $file, array $data): void
{
    $dir = dirname($file);
    if (!is_dir($dir)) {
        @mkdir($dir, 0700, true);
    }
    $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        throw new RuntimeException('encode failed');
    }
    $tmp = $dir . '/.' . basename($file) . '.' . bin2hex(random_bytes(6)) . '.tmp';
    if (file_put_contents($tmp, $json, LOCK_EX) === false) {
        throw new RuntimeException('write failed');
    }
    @chmod($tmp, 0600);
    if (!rename($tmp, $file)) {
        @unlink($tmp);
        throw new RuntimeException('rename failed');
    }
}

/**
 * Read, modify and write under a single lock.
 *
 * The callback receives the array and **must** explicitly return one of the following:
 *   - the modified array: it is written
 *   - false: write nothing (e.g. the cap is full or a condition does not hold)
 *
 * Because arrays are value types in PHP, a local change to the parameter does not propagate outward.
 * Returning null (i.e. a callback without a return) is an error, because the most common way to
 * silently break this function is exactly that: forgetting the return and writing back the same
 * unchanged input.
 *
 * The lock is taken on a separate `.lock` file so that the read-modify-write of several
 * concurrent requests is not lost and no item is lost or duplicated.
 */
function jupdate(string $file, callable $mutator)
{
    $dir = dirname($file);
    if (!is_dir($dir)) {
        @mkdir($dir, 0700, true);
    }
    $lock = @fopen($file . '.lock', 'c');
    if (!$lock) {
        throw new RuntimeException('lock failed');
    }
    flock($lock, LOCK_EX);
    try {
        $data = jread($file, []);
        $out = $mutator($data);
        if ($out === false) {
            return false;
        }
        if (!is_array($out)) {
            throw new InvalidArgumentException('jupdate mutator must return an array or false');
        }
        $data = $out;
        jwrite($file, $data);
        return true;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

function h(?string $s): string
{
    return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8');
}

function csrf_token(): string
{
    sess_start();
    if (empty($_SESSION['csrf'])) {
        $_SESSION['csrf'] = bin2hex(random_bytes(32));
    }
    return $_SESSION['csrf'];
}

function csrf_field(): string
{
    return '<input type="hidden" name="csrf" value="' . h(csrf_token()) . '">';
}

function csrf_check(): void
{
    sess_start();
    $t = $_POST['csrf'] ?? '';
    if (!is_string($t) || empty($_SESSION['csrf']) || !hash_equals($_SESSION['csrf'], $t)) {
        http_response_code(419);
        exit('CSRF');
    }
}

function authed(): bool
{
    sess_start();
    return !empty($_SESSION['auth'])
        && !empty($_SESSION['auth_at'])
        && time() - $_SESSION['auth_at'] <= SESSION_LIFETIME;
}

function require_auth(): void
{
    if (!authed()) {
        header('Location: login.php');
        exit;
    }
}

function flash(string $msg, string $type = 'ok'): void
{
    sess_start();
    $_SESSION['flash'] = ['m' => $msg, 't' => $type];
}

function flash_pop(): ?array
{
    sess_start();
    if (empty($_SESSION['flash'])) {
        return null;
    }
    $f = $_SESSION['flash'];
    unset($_SESSION['flash']);
    return $f;
}

function auth_file(): string
{
    return DATA_DIR . '/auth.json';
}

function attempts_file(): string
{
    return DATA_DIR . '/attempts.json';
}

function attempts_ip_file(): string
{
    return DATA_DIR . '/attempts_ip.json';
}

/**
 * IP comes only from REMOTE_ADDR. No spoofable header (X-Forwarded-For, X-Real-IP,
 * HTTP_CLIENT_IP, Forwarded) is read; behind a CDN the same edge IP is recorded,
 * which is enough for rate limiting.
 */
function client_ip(): string
{
    $ip = (string)($_SERVER['REMOTE_ADDR'] ?? '');
    return filter_var($ip, FILTER_VALIDATE_IP) !== false ? $ip : '';
}

/**
 * Fixed salt for hashing the IP. Kept in a file outside git (DATA_DIR/.salt);
 * without it the IP hash is reversible offline and there is no privacy
 * protection.
 */
function rate_salt(): string
{
    static $salt = null;
    if ($salt !== null) {
        return $salt;
    }
    $f = DATA_DIR . '/.salt';
    if (is_file($f)) {
        $raw = @file_get_contents($f);
        if (is_string($raw) && strlen($raw) >= 32) {
            $salt = $raw;
            return $salt;
        }
    }
    $salt = bin2hex(random_bytes(32));
    if (!is_dir(DATA_DIR)) {
        @mkdir(DATA_DIR, 0700, true);
    }
    @file_put_contents($f, $salt, LOCK_EX);
    @chmod($f, 0600);
    return $salt;
}

/**
 * Counter key: we never let a raw IP be stored in any data file. The 32-character
 * HMAC-SHA256 key is enough for rate limiting and is not reversible.
 */
function ip_key(string $ip): string
{
    // Even an empty IP is hashed, so that all requests without an address do not pile
    // into a single bucket and needlessly rate-limit each other.
    return substr(hash_hmac('sha256', $ip, rate_salt()), 0, 32);
}

/**
 * Retention limit for the origin hash — the `src` entry in reports.json and `ip`/`ip_last` in stats.json.
 *
 * These hashes are not reversible, but they still say "this request came from the same
 * origin as that one" and must not be kept for an unlimited time. 90 days is a full quarter
 * of correlation for the admin; after that only the origin label is cleared and the record
 * itself (report or client) stays in place, because the connection history is valuable
 * for troubleshooting and contains no address.
 */
const SRC_HASH_TTL_DAYS = 90;

/**
 * Is the ISO 8601 date older than the limit?
 *
 * An empty or unreadable date also counts as "old": an old entry that has lost
 * its date can no longer carry an origin label, and staying silent in this
 * case means the data stays forever.
 */
function src_hash_expired(string $iso): bool
{
    if ($iso === '') {
        return true;
    }
    $t = strtotime($iso);
    if ($t === false) {
        return true;
    }
    return (time() - $t) > (SRC_HASH_TTL_DAYS * 86400);
}

/**
 * Clearing the origin label in reports.json. Only `src` is removed; report text, errors and logs
 * remain untouched.
 *
 * Returns false when nothing has changed, so jupdate does not write to disk needlessly.
 */
function reports_prune_src(array $store): array
{
    if (!is_array($store['items'] ?? null)) {
        return $store;
    }
    foreach ($store['items'] as &$it) {
        if (!is_array($it)) {
            continue;
        }
        if (($it['src'] ?? '') !== '' && src_hash_expired((string)($it['at'] ?? ''))) {
            $it['src'] = '';
        }
    }
    unset($it);
    return $store;
}

/**
 * Login lock: per-IP only (20 failed attempts/hour from each address).
 *
 * The global cap deliberately does not lock out completely: if it did, an attacker
 * with a few dozen cheap wrong attempts could lock the admin himself out for an hour (login-DoS).
 * Instead attempt_pressure() turns the global pressure into a short delay or a 429
 * response.
 */
function attempt_allowed(): bool
{
    $now = time();
    $k = ip_key(client_ip());

    $ipa = jread(attempts_ip_file(), ['k' => []]);
    $e = is_array($ipa['k'][$k] ?? null) ? $ipa['k'][$k] : ['n' => 0, 'ts' => $now];
    return !((int)$e['n'] >= 20 && $now - (int)$e['ts'] < 3600);
}

/**
 * Cap on the artificial login delay, in seconds.
 *
 * Why so short: `sleep` in PHP pins a request to a worker for that duration.
 * If the global pressure slept for twenty seconds, an attacker with a few hundred
 * parallel requests could occupy the whole pool and take the panel down — exactly
 * what the global lock wanted to prevent. The delay must slow online guessing,
 * not become the attack weapon itself. Above the cap, instead of waiting,
 * an immediate 429 response is returned.
 */
const LOGIN_DELAY_MAX = 2;

/**
 * Number of global failed attempts from which on we no longer wait and return
 * 429. Below that, only a short delay.
 */
const LOGIN_GLOBAL_REJECT = 400;

/**
 * Returns the global pressure of the last hour:
 *   [0] extra delay in seconds (0 or LOGIN_DELAY_MAX - 1; with the base second that is
 *       1 to LOGIN_DELAY_MAX seconds in total)
 *   [1] whether to reject outright (429) instead of waiting
 *
 * The global cap deliberately does not lock out completely: if it did, an attacker
 * with a few dozen cheap wrong attempts could lock the admin himself out for an hour (login-DoS).
 */
function attempt_pressure(): array
{
    $a = jread(attempts_file(), ['n' => 0, 'ts' => 0]);
    if (time() - (int)$a['ts'] >= 3600) {
        return [0, false];
    }
    $n = (int)$a['n'];
    if ($n >= LOGIN_GLOBAL_REJECT) {
        return [0, true];
    }
    if ($n >= 100) {
        return [max(0, LOGIN_DELAY_MAX - 1), false];
    }
    return [0, false];
}

function attempt_fail(): void
{
    $now = time();
    $k = ip_key(client_ip());

    jupdate(attempts_ip_file(), function (array $st) use ($now, $k) {
        if (!is_array($st['k'] ?? null)) {
            $st['k'] = [];
        }
        // Short retention: only the one-hour window is kept.
        foreach ($st['k'] as $kk => $vv) {
            if (!is_array($vv) || $now - (int)($vv['ts'] ?? 0) >= 3600) {
                unset($st['k'][$kk]);
            }
        }
        $e = $st['k'][$k] ?? ['n' => 0, 'ts' => $now];
        if ($now - (int)$e['ts'] >= 3600) {
            $e = ['n' => 0, 'ts' => $now];
        }
        $e['n'] = (int)$e['n'] + 1;
        $st['k'][$k] = $e;
        return $st;
    });

    jupdate(attempts_file(), function (array $a) use ($now) {
        if ($now - (int)($a['ts'] ?? 0) >= 3600) {
            $a = ['n' => 0, 'ts' => $now];
        }
        $a['n'] = (int)($a['n'] ?? 0) + 1;
        return $a;
    });
}

function attempt_reset(): void
{
    $k = ip_key(client_ip());
    jupdate(attempts_ip_file(), function (array $st) use ($k) {
        if (is_array($st['k'] ?? null)) {
            unset($st['k'][$k]);
        }
        return $st;
    });
}

/**
 * JSON response headers.
 *
 * CORS is enabled only for read endpoints. `report.php`, which writes, never
 * gets CORS, so no web page from another domain can forge a report
 * through the user's browser. (The desktop client is not a browser at all and does not read this header.)
 */
function api_headers(bool $cors = false): void
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    if ($cors) {
        header('Access-Control-Allow-Origin: *');
    }
    header('X-Content-Type-Options: nosniff');
    header('Referrer-Policy: no-referrer');
}

/** HTML page security headers — call before any output. */
function security_headers(): void
{
    if (headers_sent()) {
        return;
    }
    header('X-Content-Type-Options: nosniff');
    header('X-Frame-Options: DENY');
    header('Referrer-Policy: no-referrer');
    header("Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
}

/**
 * Is a path inside the data root? (Case does not matter: on Windows
 * paths are case-insensitive and one may be recorded in a different form.)
 */
function path_is_within(string $path, string $root): bool
{
    $path = rtrim(str_replace('\\', '/', $path), '/');
    $root = rtrim(str_replace('\\', '/', $root), '/');
    if ($path === '' || $root === '') {
        return false;
    }
    if (strcasecmp($path, $root) === 0) {
        return true;
    }
    return strncasecmp($path, $root . '/', strlen($root) + 1) === 0;
}

/**
 * Is DATA_DIR inside the document root?
 *
 * The only thing to catch here is that the data folder — holding auth.json,
 * stats.json, reports.json and .salt — must be somewhere the web server serves
 * directly. In that case `.htaccess` is the only web-facing protection and on nginx or
 * any host with `AllowOverride Off` it is not executed at all; for that same reason DATA_DIR
 * was previously moved outside the document root, but whoever has not migrated must see it.
 *
 * We measure two roots:
 *   1) This program's own folder (server/). Per the README the contents of this folder land in webroot,
 *      so any path under it — like the old data/ — is inside webroot.
 *      This is the primary path because it does not depend on the web server.
 *   2) DOCUMENT_ROOT, which is more precise, but the web server must pass it to PHP
 *      (nginx sometimes does not). The second path is complementary, not the only one.
 *
 * In CLI, where nothing is served, "no" is returned.
 */
function data_dir_inside_webroot(): bool
{
    static $memo = null;
    if ($memo !== null) {
        return $memo;
    }

    $raw = (string)DATA_DIR;
    $real = realpath($raw);
    $target = $real !== false ? $real : $raw;
    if ($target === '') {
        return $memo = false;
    }

    $roots = [];
    $self = realpath(__DIR__);
    if ($self !== false) {
        $roots[] = $self;
    }
    $doc = (string)($_SERVER['DOCUMENT_ROOT'] ?? '');
    if ($doc !== '') {
        $d = realpath($doc);
        $roots[] = $d !== false ? $d : $doc;
    }

    foreach ($roots as $r) {
        if (path_is_within($target, $r)) {
            return $memo = true;
        }
    }
    return $memo = false;
}

/**
 * Per-IP rate limit for writing stats; without it anyone with a random GUID can
 * grow stats.json forever (each write = rewriting the whole file).
 */
function rate_allow(string $ip, int $max, int $windowSec): bool
{
    if ($ip === '') {
        return true;
    }
    // The raw IP is not stored: key = salted hash, kept only until the end of the
    // retention window (each stale row is cleared immediately).
    $k = ip_key($ip);
    $f = DATA_DIR . '/ratelimit.json';
    $lock = @fopen(DATA_DIR . '/ratelimit.lock', 'c');
    if ($lock) {
        flock($lock, LOCK_EX);
    }
    try {
        $now = time();
        $st = jread($f, ['k' => []]);
        if (!is_array($st['k'] ?? null)) {
            $st['k'] = [];
        }
        foreach ($st['k'] as $kk => $vv) {
            if (!is_array($vv) || $now - (int)($vv['ts'] ?? 0) >= $windowSec) {
                unset($st['k'][$kk]);
            }
        }
        $e = $st['k'][$k] ?? ['n' => 0, 'ts' => $now];
        if ($now - (int)$e['ts'] >= $windowSec) {
            $e = ['n' => 0, 'ts' => $now];
        }
        if ((int)$e['n'] >= $max) {
            $st['k'][$k] = $e;
            jwrite($f, $st);
            return false;
        }
        $e['n'] = (int)$e['n'] + 1;
        $st['k'][$k] = $e;
        jwrite($f, $st);
        return true;
    } finally {
        if ($lock) {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}

function ver_safe(string $v): string
{
    $v = preg_replace('/[^0-9A-Za-z.\-]/', '', trim($v)) ?? '';
    return substr($v, 0, 32);
}

/**
 * Chunked upload limits — shared by the version form (version.php, for the
 * client-side check) and the chunk endpoint (admin/upart.php).
 *
 * A chunk stays under 1.1MB so it survives any per-request proxy limit
 * (nginx client_max_body_size default is 1MB); the assembled file may reach
 * UPART_TOTAL_MAX as long as every individual request remains small.
 */
const UPART_CHUNK_MAX = 1100000;
const UPART_TOTAL_MAX = 134217728;    // 128 MB
const UPART_CHUNKS_MAX = 1024;
const UPART_STALE_SEC = 7200;

/**
 * Publishing a version — the single write path used by both the classic form
 * (version.php) and the chunked uploader (admin/upart.php), so the stored JSON
 * shape and the audit line can never drift apart.
 *
 * $file is a name inside DL_DIR (upload mode), $url a direct https link.
 * The sha256 is the hash of the file the *client* downloads.
 */
function version_publish(
    string $latest,
    string $notes,
    bool $force,
    bool $active,
    ?string $url,
    ?string $file,
    string $sha,
    int $size,
    string $countries,
    string $langs
): void {
    jwrite(DATA_DIR . '/version.json', [
        'latest' => $latest,
        'notes' => str_cut($notes, 4000),
        'sha256' => $sha,
        'force' => $force,
        'active' => $active,
        'published' => date('c'),
        'url' => $url,
        'file' => $file,
        'size' => $size,
        'countries' => $countries,
        'langs' => $langs,
    ]);
    audit_log('publish', 'نسخه ' . $latest . ($active ? ' منتشر شد' : ' ذخیره شد (غیرفعال)') . ' — ' . (string)($file ?: (string)$url));
}

function lang_ok(?string $lang): string
{
    $lang = strtolower(trim((string)$lang));
    return preg_match('/^[a-z]{2}(-[a-z]{2})?$/', $lang) ? $lang : 'fa';
}

function country_client(): string
{
    $c = strtoupper(preg_replace('/[^A-Za-z]/', '', (string)($_GET['country'] ?? '')) ?? '');
    return preg_match('/^[A-Z]{2}$/', $c) ? $c : '';
}

function country_codes(string $s): array
{
    $out = [];
    foreach (explode(',', $s) as $c) {
        $c = strtoupper(trim($c));
        if (preg_match('/^[A-Z]{2}$/', $c)) {
            $out[] = $c;
        }
    }
    return array_values(array_unique($out));
}

function country_field(): string
{
    return implode(',', country_codes((string)($_POST['countries'] ?? '')));
}

function country_ok(string $list, string $client): bool
{
    $codes = country_codes($list);
    if (!$codes) {
        return true;
    }
    if ($client === '') {
        return false;
    }
    return in_array($client, $codes, true);
}

function langs_all(): array
{
    return ['fa', 'en', 'ar', 'de', 'es', 'fr', 'hi', 'ja', 'ko', 'pt-br', 'ru', 'tr', 'ur', 'zh'];
}

function langs_parse(string $s): array
{
    $out = [];
    foreach (explode(',', $s) as $c) {
        $c = strtolower(trim($c));
        if (in_array($c, langs_all(), true)) {
            $out[] = $c;
        }
    }
    return array_values(array_unique($out));
}

function langs_field(): string
{
    if (isset($_POST['lang_all'])) {
        return '*';
    }
    $sel = $_POST['langs'] ?? [];
    if (!is_array($sel)) {
        $sel = [(string)$sel];
    }
    $codes = [];
    foreach ($sel as $c) {
        $c = strtolower(trim((string)$c));
        if (in_array($c, langs_all(), true)) {
            $codes[] = $c;
        }
    }
    $codes = array_values(array_unique($codes));
    return $codes ? implode(',', $codes) : '*';
}

function item_langs(array $it): array
{
    if (!empty($it['langs'])) {
        $s = (string)$it['langs'];
        return $s === '*' ? [] : langs_parse($s);
    }
    $old = strtolower((string)($it['lang'] ?? '*'));
    return ($old === '*' || $old === '') ? [] : [$old];
}

function lang_matches(array $allowed, string $client): bool
{
    if (!$allowed) {
        return true;
    }
    return in_array(strtolower($client), $allowed, true);
}

function lang_picker(): string
{
    $list = [
        'fa' => 'فارسی', 'en' => 'انگلیسی', 'ar' => 'عربی', 'de' => 'آلمانی',
        'es' => 'اسپانیایی', 'fr' => 'فرانسوی', 'hi' => 'هندی', 'ja' => 'ژاپنی',
        'ko' => 'کره‌ای', 'pt-br' => 'پرتغالی برزیل', 'ru' => 'روسی', 'tr' => 'ترکی',
        'ur' => 'اردو', 'zh' => 'چینی',
    ];
    $html = '<label>زبان‌های هدف (زبان‌های برنامه)</label>';
    $html .= '<div class="check"><input type="checkbox" name="lang_all" id="lang_all" checked onchange="if(this.checked){document.querySelectorAll(\'.langcb\').forEach(function(c){c.checked=false;})}"><label for="lang_all" style="margin:0">همه زبان‌ها</label></div>';
    $html .= '<div style="display:flex;flex-wrap:wrap;gap:4px 16px;margin:4px 0 8px">';
    foreach ($list as $k => $v) {
        $id = 'lg_' . str_replace('-', '_', $k);
        $html .= '<span style="white-space:nowrap"><input type="checkbox" class="langcb" name="langs[]" value="' . $k . '" id="' . $id . '" onchange="if(this.checked){document.getElementById(\'lang_all\').checked=false;}"><label for="' . $id . '" style="margin:0">' . $v . '</label></span>';
    }
    $html .= '</div>';
    return $html;
}

function stats_touch(string $id, array $meta = []): void
{
    if (!preg_match('/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/', $id)) {
        return;
    }
    $id = strtolower($id);
    $file = DATA_DIR . '/stats.json';
    $lock = @fopen(DATA_DIR . '/stats.lock', 'c');
    if ($lock) {
        flock($lock, LOCK_EX);
    }
    try {
        $st = jread($file, ['clients' => []]);
        if (!is_array($st['clients'] ?? null)) {
            $st['clients'] = [];
        }
        $now = date('c');
        $isNew = false;
        if (isset($st['clients'][$id]) && is_array($st['clients'][$id])) {
            $st['clients'][$id]['last'] = $now;
        } else {
            // Input cap: forged GUIDs cannot grow the file forever.
            if (count($st['clients']) >= 5000) {
                return;
            }
            $st['clients'][$id] = ['first' => $now, 'last' => $now];
            $isNew = true;
        }
        foreach ($meta as $k => $v) {
            if ($v === null || $v === '') {
                continue;
            }
            $st['clients'][$id][$k] = $v;
        }
        // Clearing the origin label of clients that no longer check in. The record itself stays;
        // only the origin hash is removed. The row of this very requester is the freshest (last was just
        // set to now), so its label is never cleared in this pass.
        foreach ($st['clients'] as &$row) {
            if (!is_array($row)) {
                continue;
            }
            $hasSrc = (($row['ip_last'] ?? '') !== '') || (($row['ip'] ?? '') !== '');
            if ($hasSrc && src_hash_expired((string)($row['last'] ?? ''))) {
                $row['ip_last'] = '';
                $row['ip'] = '';
            }
        }
        unset($row);
        jwrite($file, $st);
        // Daily chart counters go to their own file *after* the main stats, so
        // a chart-side failure can never cost the check-in itself.
        daily_touch($id, $isNew);
    } finally {
        if ($lock) {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}

function stats_find(string $id): ?array
{
    $id = strtolower(trim($id));
    if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $id)) {
        return null;
    }
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $c = $st['clients'][$id] ?? null;
    if (!is_array($c)) {
        return null;
    }
    $c['id'] = $id;
    return $c;
}

function stats_sorted(): array
{
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $out = [];
    foreach (($st['clients'] ?? []) as $id => $c) {
        if (!is_array($c)) {
            continue;
        }
        $c['id'] = (string)$id;
        $out[] = $c;
    }
    usort($out, function ($a, $b) {
        return strcmp((string)($b['last'] ?? ''), (string)($a['last'] ?? ''));
    });
    return $out;
}

function stats_summary(): array
{
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $now = time();
    $installs = 0;
    $a24 = 0;
    $a7 = 0;
    $online = 0;
    $conns = 0;
    foreach (($st['clients'] ?? []) as $c) {
        if (!is_array($c)) {
            continue;
        }
        $installs++;
        $conns += max(0, (int)($c['conn'] ?? 0));
        $t = strtotime((string)($c['last'] ?? '')) ?: 0;
        if ($t > 0 && $now - $t <= 1200) {
            $online++;
        }
        if ($t > 0 && $now - $t <= 86400) {
            $a24++;
        }
        if ($t > 0 && $now - $t <= 604800) {
            $a7++;
        }
    }
    return [
        'installs' => $installs,
        'active24' => $a24,
        'active7' => $a7,
        'online' => $online,
        'connections' => $conns,
    ];
}

function stats_online(array $c, int $window = 1200): bool
{
    $t = strtotime((string)($c['last'] ?? '')) ?: 0;
    return $t > 0 && time() - $t <= $window;
}

/**
 * Daily counters for the dashboard charts (data/daily.json):
 *   days["2026-10-29"] = {"new": 3, "active": {"<guid>": 1, ...}}
 * Downloads per day are already counted by api/dl.php into dlstats.json.
 *
 * Two retentions in one file: counters stay 90 days (same window dlstats
 * keeps), the active-id sets only 30 — a set holds one GUID per client that
 * checked in that day and the whole file is rewritten on every check-in.
 *
 * Counting must never break a check-in: every failure is swallowed.
 */
function daily_touch(string $id, bool $isNew): void
{
    if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $id)) {
        return;
    }
    $day = date('Y-m-d');
    $cutCounter = date('Y-m-d', time() - 90 * 86400);
    $cutActive = date('Y-m-d', time() - 30 * 86400);
    try {
        jupdate(DATA_DIR . '/daily.json', function (array $d) use ($id, $isNew, $day, $cutCounter, $cutActive) {
            $days = is_array($d['days'] ?? null) ? $d['days'] : [];
            $slot = is_array($days[$day] ?? null) ? $days[$day] : [];
            if ($isNew) {
                $slot['new'] = (int)($slot['new'] ?? 0) + 1;
            }
            $set = is_array($slot['active'] ?? null) ? $slot['active'] : [];
            $set[$id] = 1;
            $slot['active'] = $set;
            $days[$day] = $slot;
            foreach (array_keys($days) as $old) {
                if ((string)$old < $cutCounter) {
                    unset($days[$old]);
                } elseif ((string)$old < $cutActive) {
                    unset($days[$old]['active']);
                    if ($days[$old] === []) {
                        unset($days[$old]);
                    }
                }
            }
            return ['days' => $days];
        });
    } catch (Throwable $e) {
        // A lost chart point beats a lost check-in.
    }
}

/**
 * Panel audit trail (data/audit.json): what was changed, by whom, from which
 * origin — last 500 entries are kept, so the file cannot grow forever.
 *
 * The origin is stored as the same one-way key the login limiter uses
 * (ip_key), so the trail correlates events without becoming a second,
 * permanent copy of addresses.
 *
 * Logging must never break the action being recorded.
 */
function audit_log(string $action, string $detail = ''): void
{
    $entry = [
        't' => date('c'),
        'a' => str_cut($action, 40),
        'd' => str_cut($detail, 300),
        'u' => (string)($_SESSION['username'] ?? ''),
        'k' => substr(ip_key(client_ip()), 0, 12),
    ];
    try {
        jupdate(DATA_DIR . '/audit.json', function (array $d) use ($entry) {
            $items = is_array($d['items'] ?? null) ? $d['items'] : [];
            $items[] = $entry;
            if (count($items) > 500) {
                $items = array_slice($items, -500);
            }
            return ['items' => $items];
        });
    } catch (Throwable $e) {
        // An audit failure must not break the action it describes.
    }
}

/**
 * One-click backup of DATA_DIR: an archive with every data file (auth,
 * stats, reports, counters, audit), minus what is transient — flock files
 * and PHP sessions.
 *
 * Format is chosen by capability: ZipArchive when the extension exists,
 * otherwise a .tar.gz built with PharData (both read without extra tools;
 * Windows 10/11 ship tar). Returns the final path, entry count and size.
 */
function backup_build(string $destBase): array
{
    $files = [];
    $it = new RecursiveIteratorIterator(
        new RecursiveDirectoryIterator(DATA_DIR, FilesystemIterator::SKIP_DOTS)
    );
    foreach ($it as $f) {
        if (!$f->isFile()) {
            continue;
        }
        $rel = str_replace('\\', '/', substr($f->getPathname(), strlen(DATA_DIR) + 1));
        if ($rel === 'backup.json') {
            continue; // meta about backups, not backup content
        }
        if (preg_match('#(^|/)sessions(/|$)#', $rel) || preg_match('/\.lock$/', $rel)) {
            continue;
        }
        $files[] = [$f->getPathname(), $rel];
    }
    if (!$files) {
        throw new RuntimeException('data dir is empty');
    }

    if (class_exists('ZipArchive')) {
        $path = $destBase . '.zip';
        $zip = new ZipArchive();
        if ($zip->open($path, ZipArchive::CREATE | ZipArchive::OVERWRITE) !== true) {
            throw new RuntimeException('zip create failed');
        }
        foreach ($files as [$abs, $rel]) {
            $zip->addFile($abs, $rel);
        }
        $zip->close();
        $fmt = 'zip';
    } else {
        // PharData honors the .tar name but keeps members uncompressed no
        // matter what the extension says, so the tar is built first and then
        // gzipped by hand — otherwise the ".tar.gz" would be plain tar.
        $tar = $destBase . '.tar';
        $path = $destBase . '.tar.gz';
        @unlink($tar);
        @unlink($path);
        $phar = new PharData($tar);
        foreach ($files as [$abs, $rel]) {
            $phar->addFile($abs, $rel);
        }
        unset($phar); // release the archive before reading it back
        $in = @fopen($tar, 'rb');
        if ($in === false) {
            throw new RuntimeException('tar read failed');
        }
        $out = gzopen($path, 'wb6');
        if ($out === false) {
            fclose($in);
            throw new RuntimeException('gzip write failed');
        }
        while (!feof($in)) {
            $chunk = fread($in, 262144);
            if ($chunk === false) {
                break;
            }
            if ($chunk !== '') {
                gzwrite($out, $chunk);
            }
        }
        gzclose($out);
        fclose($in);
        @unlink($tar);
        $fmt = 'tar.gz';
    }
    return ['path' => $path, 'files' => count($files), 'bytes' => (int)@filesize($path), 'fmt' => $fmt];
}

function ip_public(string $ip): bool
{
    if (filter_var($ip, FILTER_VALIDATE_IP) === false) {
        return false;
    }
    return filter_var($ip, FILTER_VALIDATE_IP, FILTER_FLAG_NO_PRIV_RANGE | FILTER_FLAG_NO_RES_RANGE) !== false;
}

function ip_info(string $ip): ?array
{
    if (!ip_public($ip)) {
        return ['isp' => '', 'asn' => 0, 'org' => ''];
    }
    $ctx = stream_context_create([
        'http' => [
            'timeout' => 3,
            'header' => "User-Agent: RoxaPanel/1.0\r\nAccept: application/json\r\n",
            'ignore_errors' => true,
        ],
    ]);
    $raw = @file_get_contents('https://ipwho.is/' . rawurlencode($ip), false, $ctx);
    if ($raw === false || $raw === '') {
        return null;
    }
    $j = json_decode($raw, true);
    if (!is_array($j)) {
        return null;
    }
    if (empty($j['success'])) {
        return ['isp' => '', 'asn' => 0, 'org' => ''];
    }
    $conn = is_array($j['connection'] ?? null) ? $j['connection'] : [];
    $isp = trim((string)($conn['isp'] ?? ''));
    $org = trim((string)($conn['org'] ?? ''));
    $asn = (int)($conn['asn'] ?? 0);
    return ['isp' => str_cut($isp, 80), 'asn' => max(0, $asn), 'org' => str_cut($org, 80)];
}

function ip_info_save(string $id, array $info): void
{
    stats_touch($id, [
        'isp_done' => 1,
        'isp' => $info['isp'] !== '' ? $info['isp'] : null,
        'asn' => $info['asn'] > 0 ? $info['asn'] : null,
        'org' => $info['org'] !== '' ? $info['org'] : null,
    ]);
}

/** Hourly cap on ipwho.is calls; without it every forged `id` makes a server-side request. */
function ipinfo_allow(): bool
{
    $f = DATA_DIR . '/ipinfo_quota.json';
    $q = jread($f, ['h' => '', 'n' => 0]);
    $h = gmdate('YmdH');
    if ((string)($q['h'] ?? '') !== $h) {
        $q = ['h' => $h, 'n' => 0];
    }
    if ((int)($q['n'] ?? 0) >= 60) {
        return false;
    }
    $q['n'] = (int)($q['n'] ?? 0) + 1;
    jwrite($f, $q);
    return true;
}

/**
 * ISP/ASN enrichment for unfinished rows. It deliberately works with the stored IP,
 * and since the raw IP is no longer stored, this function does nothing for new rows —
 * ISP info is recorded at check.php time with the in-memory IP. It still works
 * for old stored data.
 */
function ip_fill(array &$rows, int $cap = 10, float $budget = 4.0): void
{
    $deadline = microtime(true) + $budget;
    $done = 0;
    foreach ($rows as &$c) {
        if ($done >= $cap || microtime(true) >= $deadline) {
            break;
        }
        $id = (string)($c['id'] ?? '');
        $ip = (string)($c['ip'] ?? '');
        if ($id === '' || !empty($c['isp_done'])) {
            continue;
        }
        if ($ip === '') {
            continue;
        }
        if (!ip_public($ip)) {
            stats_touch($id, ['isp_done' => 1]);
            $c['isp_done'] = 1;
            continue;
        }
        $info = ip_info($ip);
        if ($info === null) {
            continue;
        }
        ip_info_save($id, $info);
        $c['isp_done'] = 1;
        $c['isp'] = $info['isp'];
        $c['asn'] = $info['asn'];
        $c['org'] = $info['org'];
        $done++;
    }
    unset($c);
}

/**
 * https only. Links `javascript:`, `data:`, `vbscript:` and `http:` are not
 * accepted; empty output means "empty link allowed", null means "invalid".
 */
function safe_url(string $url): ?string
{
    $url = trim($url);
    if ($url === '') {
        return '';
    }
    if (!preg_match('#^https://[^\s<>"\']+$#i', $url)) {
        return null;
    }
    $p = parse_url($url);
    if (!is_array($p) || empty($p['host'])) {
        return null;
    }
    return $url;
}

/**
 * Short display of the "origin key" in the panel. This is no longer an IP; it cannot be
 * reversed to a real address, but it still says "the same previous origin" or "another source".
 */
function src_short(string $src): string
{
    return $src === '' ? '' : substr($src, 0, 12);
}

function str_cut(string $s, int $len): string
{
    if (function_exists('mb_substr')) {
        return mb_substr($s, 0, $len);
    }
    if (preg_match('/^.{0,' . $len . '}/us', $s, $m)) {
        return $m[0];
    }
    return substr($s, 0, $len);
}

function str_len(string $s): int
{
    if (function_exists('mb_strlen')) {
        return mb_strlen($s);
    }
    $n = preg_match_all('/./us', $s);
    return $n === false ? strlen($s) : $n;
}
