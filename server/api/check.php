<?php
require_once __DIR__ . '/../lib.php';
api_headers(true);

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
$pendingIsp = null;   // ip to enrich *after* the response is out
$pendingId = '';
if ($id !== '') {
    $ipRemote = (string)($_SERVER['REMOTE_ADDR'] ?? '');
    $ipOk = filter_var($ipRemote, FILTER_VALIDATE_IP) !== false ? $ipRemote : '';
    $direct = isset($_GET['direct']) && $_GET['direct'] === '1';
    $os = trim((string)($_GET['os'] ?? ''));

    $dev = trim((string)($_GET['dev'] ?? ''));
    $dev = str_cut(preg_replace('/[\x00-\x1F\x7F]/', '', $dev) ?? '', 64);

    $conn = max(0, (int)($_GET['conn'] ?? 0));
    if ($conn > 0) {
        $prev = stats_find($id);
        if (is_array($prev)) {
            $conn = max($conn, (int)($prev['conn'] ?? 0));
        }
    }

    // Writing stats is per-IP capped; the update response is returned either way.
    // The raw IP is not stored — only its salted hash, to tell "the same earlier source".
    if (rate_allow($ipOk !== '' ? $ipOk : 'unknown', 60, 60)) {
        stats_touch($id, [
            'ip_last' => $ipOk !== '' ? ip_key($ipOk) : '',
            'ip' => ($direct && $ipOk !== '') ? ip_key($ipOk) : '',
            'os' => $os !== '' ? str_cut($os, 120) : '',
            'country' => $cc,
            'lang' => $lang,
            'ver' => $current,
            'dev' => $dev !== '' ? $dev : null,
            'conn' => $conn > 0 ? $conn : null,
        ]);

        // ISP lookup is an external call (up to 3s) and is deferred below: the
        // client must get its answer first, not wait behind ipwho.is.
        if ($direct && $ipOk !== '') {
            $row = stats_find($id);
            if (is_array($row) && empty($row['isp_done']) && ipinfo_allow()) {
                $pendingIsp = $ipOk;
                $pendingId = $id;
            }
        }
    }
}

// Send the JSON before any slow follow-up work. Content-Length is set
// explicitly so the client learns the response is complete without waiting
// for the connection to close — the script may keep running for the ISP
// lookup below, and clients must not wait for that.
$send = static function (array $payload): void {
    $json = json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    header('Content-Length: ' . strlen($json));
    echo $json;
    if (ob_get_level() > 0) {
        @ob_end_flush();
    }
    flush();
};

// The deferred ISP lookup runs after the client already has its answer.
$enrich = static function () use ($pendingIsp, $pendingId): void {
    if ($pendingIsp === null) {
        return;
    }
    $info = ip_info($pendingIsp);
    if ($info !== null) {
        ip_info_save($pendingId, $info);
    }
};

if (empty($ver['active']) || empty($ver['latest'])) {
    $send(['ok' => true, 'latest' => null]);
    $enrich();
    exit;
}

$url = $ver['url'];
if (empty($url) && !empty($ver['file'])) {
    $url = BASE_URL . '/dl/' . rawurlencode($ver['file']);
}

$updateAvailable = country_ok((string)($ver['countries'] ?? ''), $cc)
    && lang_matches(langs_parse((string)($ver['langs'] ?? '')), $lang)
    && ($current === '' ? true : version_compare((string)$ver['latest'], $current, '>'));

$send([
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
]);
$enrich();
