<?php
require_once __DIR__ . '/../lib.php';

// خروج باید POST باشد: خروجِ GET از طریقِ یک تصویر یا لینک در هر صفحه‌ای
// قابلِ کلیک‌شدن است و session را می‌شکند (logout CSRF).
if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    http_response_code(405);
    header('Allow: POST');
    exit;
}

sess_start();
if (authed()) {
    csrf_check();
}

$_SESSION = [];
$_SESSION = [];
if (ini_get('session.use_cookies')) {
    $p = session_get_cookie_params();
    setcookie(session_name(), '', time() - 42000, $p['path'], $p['domain'], (bool)$p['secure'], (bool)$p['httponly']);
}
session_destroy();
session_regenerate_id(true);

header('Location: login.php');
exit;
