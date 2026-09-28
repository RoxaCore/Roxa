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
        if (isset($st['clients'][$id]) && is_array($st['clients'][$id])) {
            $st['clients'][$id]['last'] = $now;
        } else {
            $st['clients'][$id] = ['first' => $now, 'last' => $now];
        }
        foreach ($meta as $k => $v) {
            if ($v === null || $v === '') {
                continue;
            }
            $st['clients'][$id][$k] = $v;
        }
        jwrite($file, $st);
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
    foreach (($st['clients'] ?? []) as $c) {
        if (!is_array($c)) {
            continue;
        }
        $installs++;
        $t = strtotime((string)($c['last'] ?? '')) ?: 0;
        if ($t > 0 && $now - $t <= 86400) {
            $a24++;
        }
        if ($t > 0 && $now - $t <= 604800) {
            $a7++;
        }
    }
    return ['installs' => $installs, 'active24' => $a24, 'active7' => $a7];
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
