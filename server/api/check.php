<?php
require_once __DIR__ . '/../lib.php';
api_headers();

$ver = jread(DATA_DIR . '/version.json', [
    'latest' => null,
    'notes' => '',
    'sha256' => '',
    'force' => false,
    'active' => false,
    'published' => null,
    'url' => null,
    'file' => null,
    'countries' => '',
    'langs' => '*',
]);

$current = isset($_GET['v']) ? ver_safe((string)$_GET['v']) : '';
$lang = lang_ok(isset($_GET['lang']) ? (string)$_GET['lang'] : 'fa');
$cc = country_client();

$id = (string)($_GET['id'] ?? '');
if ($id !== '') {
    $ipRemote = (string)($_SERVER['REMOTE_ADDR'] ?? '');
    $ipOk = filter_var($ipRemote, FILTER_VALIDATE_IP) !== false ? $ipRemote : '';
    $direct = isset($_GET['direct']) && $_GET['direct'] === '1';
    $os = trim((string)($_GET['os'] ?? ''));
    stats_touch($id, [
        'ip_last' => $ipOk,
        'ip' => $direct ? $ipOk : '',
        'os' => $os !== '' ? str_cut($os, 120) : '',
        'country' => $cc,
        'lang' => $lang,
        'ver' => $current,
    ]);
}

if (empty($ver['active']) || empty($ver['latest'])) {
    echo json_encode(['ok' => true, 'latest' => null], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    exit;
}

$url = $ver['url'];
if (empty($url) && !empty($ver['file'])) {
    $url = BASE_URL . '/dl/' . rawurlencode($ver['file']);
}

$updateAvailable = country_ok((string)($ver['countries'] ?? ''), $cc)
    && lang_matches(langs_parse((string)($ver['langs'] ?? '')), $lang)
    && ($current === '' ? true : version_compare((string)$ver['latest'], $current, '>'));

echo json_encode([
    'ok' => true,
    'latest' => (string)$ver['latest'],
    'current' => $current,
    'update_available' => $updateAvailable,
    'force' => !empty($ver['force']),
    'notes' => (string)($ver['notes'] ?? ''),
    'url' => $url,
    'sha256' => (string)($ver['sha256'] ?? ''),
    'size' => (int)($ver['size'] ?? 0),
    'published' => $ver['published'] ?? null,
    'lang' => $lang,
], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
