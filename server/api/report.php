<?php
require_once __DIR__ . '/../lib.php';
api_headers();

function report_json(array $body, int $code): void
{
    http_response_code($code);
    echo json_encode($body, JSON_UNESCAPED_UNICODE);
    exit;
}

if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    report_json(['ok' => false, 'error' => 'method'], 405);
}

$raw = (string)file_get_contents('php://input');
if ($raw === '' || strlen($raw) > 30000) {
    report_json(['ok' => false, 'error' => 'size'], 400);
}
$data = json_decode($raw, true);
if (!is_array($data)) {
    report_json(['ok' => false, 'error' => 'json'], 400);
}

$ipRemote = (string)($_SERVER['REMOTE_ADDR'] ?? '');
$ip = filter_var($ipRemote, FILTER_VALIDATE_IP) !== false ? $ipRemote : '';
// سقف per-IP: ۱۰ گزارش در ساعت — مانع سیل گزارش‌های جعلی.
if (!rate_allow('report:' . ($ip !== '' ? $ip : 'unknown'), 10, 3600)) {
    report_json(['ok' => false, 'error' => 'rate'], 429);
}

$strip = static fn(string $s): string => preg_replace('/[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]/', '', $s) ?? '';

$type = (string)($data['type'] ?? '') === 'crash' ? 'crash' : 'manual';
$ver = str_cut(preg_replace('/[^0-9A-Za-z.+\-]/', '', (string)($data['ver'] ?? '')) ?? '', 32);
$os = str_cut($strip((string)($data['os'] ?? '')), 120);
$id = (string)($data['id'] ?? '');
if (!preg_match('/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/', $id)) {
    $id = '';
}
$message = str_cut($strip((string)($data['message'] ?? '')), 2000);
$error = str_cut($strip((string)($data['error'] ?? '')), 4000);
$log = str_cut($strip((string)($data['log'] ?? '')), 4000);

if ($message === '' && $error === '') {
    report_json(['ok' => false, 'error' => 'empty'], 400);
}

try {
    $file = DATA_DIR . '/reports.json';
    $store = jread($file, ['seq' => 0, 'items' => []]);
    $store['seq'] = (int)($store['seq'] ?? 0) + 1;
    $store['items'][] = [
        'n' => (int)$store['seq'],
        'type' => $type,
        'ver' => $ver,
        'os' => $os,
        'id' => $id,
        'message' => $message,
        'error' => $error,
        'log' => $log,
        'ip' => $ip,
        'at' => date('c'),
        'seen' => false,
    ];
    // سقف نگهداری: قدیمی‌ترین‌ها حذف شوند تا فایل رشد نکند.
    if (count($store['items']) > 500) {
        $store['items'] = array_slice($store['items'], -500);
    }
    jwrite($file, $store);
} catch (Throwable $e) {
    report_json(['ok' => false, 'error' => 'write'], 500);
}

report_json(['ok' => true, 'n' => (int)$store['seq']], 200);
