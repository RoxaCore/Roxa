<?php
require_once __DIR__ . '/../lib.php';
api_headers(true);

$store = jread(DATA_DIR . '/announcements.json', ['seq' => 0, 'items' => []]);
$since = isset($_GET['since']) ? (int)$_GET['since'] : 0;
$lang = lang_ok(isset($_GET['lang']) ? (string)$_GET['lang'] : 'fa');
$cc = country_client();

$items = [];
foreach (($store['items'] ?? []) as $it) {
    if (empty($it['active'])) {
        continue;
    }
    if ((int)$it['id'] <= $since) {
        continue;
    }
    if (!country_ok((string)($it['countries'] ?? ''), $cc)) {
        continue;
    }
    if (!lang_matches(item_langs($it), $lang)) {
        continue;
    }
    $items[] = [
        'id' => (int)$it['id'],
        'title' => (string)($it['title'] ?? ''),
        'body' => (string)($it['body'] ?? ''),
        'level' => (string)($it['level'] ?? 'info'),
        'url' => (string)(safe_url((string)($it['url'] ?? '')) ?? ''),
        'created' => (string)($it['created'] ?? ''),
    ];
}

usort($items, function ($a, $b) {
    return $b['id'] <=> $a['id'];
});

echo json_encode(['ok' => true, 'items' => array_slice($items, 0, 50)], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
