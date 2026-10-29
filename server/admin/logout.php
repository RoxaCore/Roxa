<?php
require_once __DIR__ . '/../lib.php';

// Logout must be POST: a GET logout can be triggered via an image or link on
// any page and breaks the session (logout CSRF).
if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
    http_response_code(405);
    header('Allow: POST');
    exit;
}

sess_start();
if (authed()) {
    csrf_check();
    audit_log('logout', 'خروج از پنل');
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
