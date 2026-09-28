<?php
require_once __DIR__ . '/config.php';

date_default_timezone_set(TZ);

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

function jread(string $file, array $default): array
{
    if (!is_file($file)) {
        return $default;
    }
    $raw = @file_get_contents($file);
    if ($raw === false || $raw === '') {
        return $default;
    }
    $data = json_decode($raw, true);
    return is_array($data) ? $data : $default;
}

function jwrite(string $file, array $data): void
{
    $dir = dirname($file);
    if (!is_dir($dir)) {
        @mkdir($dir, 0755, true);
    }
    $fp = @fopen($file, 'c+');
    if (!$fp) {
        throw new RuntimeException('write failed');
    }
    flock($fp, LOCK_EX);
    ftruncate($fp, 0);
    rewind($fp);
    fwrite($fp, json_encode($data, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES));
    fflush($fp);
    flock($fp, LOCK_UN);
    fclose($fp);
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

function attempt_allowed(): bool
{
    $a = jread(attempts_file(), ['n' => 0, 'ts' => 0]);
    if ($a['n'] >= 8 && time() - $a['ts'] < 900) {
        return false;
    }
    return true;
}

function attempt_fail(): void
{
    $a = jread(attempts_file(), ['n' => 0, 'ts' => time()]);
    if (time() - $a['ts'] >= 900) {
        $a = ['n' => 0, 'ts' => time()];
    }
    $a['n'] = (int)$a['n'] + 1;
    jwrite(attempts_file(), $a);
}

function attempt_reset(): void
{
    jwrite(attempts_file(), ['n' => 0, 'ts' => 0]);
}

function api_headers(): void
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    header('Access-Control-Allow-Origin: *');
}

function ver_safe(string $v): string
{
    $v = preg_replace('/[^0-9A-Za-z.\-]/', '', trim($v)) ?? '';
    return substr($v, 0, 32);
}

function lang_ok(?string $lang): string
{
    $lang = strtolower(trim((string)$lang));
    return preg_match('/^[a-z]{2}(-[a-z]{2})?$/', $lang) ? $lang : 'fa';
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
