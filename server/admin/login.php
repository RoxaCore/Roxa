<?php
require_once __DIR__ . '/../lib.php';
sess_start();
security_headers();

if (authed()) {
    header('Location: index.php');
    exit;
}
if (!is_file(auth_file())) {
    header('Location: index.php');
    exit;
}

$err = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    if (!attempt_allowed()) {
        $err = 'تلاش‌های ناموفق زیاد بود. کمی بعد دوباره تلاش کنید.';
    } else {
        $pw = (string)($_POST['password'] ?? '');
        $a = jread(auth_file(), []);
        if (!empty($a['hash']) && password_verify($pw, (string)$a['hash'])) {
            attempt_reset();
            session_regenerate_id(true);
            $_SESSION['auth'] = true;
            $_SESSION['auth_at'] = time();
            $_SESSION['username'] = 'admin';
            audit_log('login', 'ورود موفق به پنل');
            header('Location: index.php');
            exit;
        }
        attempt_fail();
        // The global cap never fully locks so an attacker cannot lock the admin
        // out with a cheap parallel flood. Only a short delay, and above the
        // attack cap an immediate 429 — not a long wait that occupies a worker.
        [$extra, $reject] = attempt_pressure();
        if ($reject) {
            http_response_code(429);
            $err = 'تلاش‌های ناموفق زیاد بود. کمی بعد دوباره تلاش کنید.';
        } else {
            // Base second + global pressure delay; always within the 1 to LOGIN_DELAY_MAX range.
            sleep(1 + $extra);
            $err = 'رمز عبور اشتباه است.';
        }
    }
}

echo '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">';
echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
echo '<title>ورود — ' . h(APP_NAME) . ' Panel</title>';
echo '<link href="/fonts/vazirmatn.css" rel="stylesheet">';
echo '<style>
:root{--bg:#0b0f1a;--card:#141a2a;--line:#1f2937;--tx:#e5e7eb;--mut:#9ca3af;--ac:#7c3aed}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;font-size:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:28px}
h1{font-size:20px;margin-bottom:4px}
.sub{color:var(--mut);font-size:12px;margin-bottom:18px}
label{display:block;color:var(--mut);font-size:12px;margin:12px 0 5px}
input[type=password],input[type=text]{width:100%;background:#0d1322;border:1px solid var(--line);color:var(--tx);border-radius:9px;padding:10px 12px;font-family:inherit;font-size:14px}
input:focus{outline:none;border-color:var(--ac)}
.btn{width:100%;background:var(--ac);color:#fff;border:none;border-radius:9px;padding:11px;font-family:inherit;font-size:15px;cursor:pointer;margin-top:18px}
.btn:hover{background:#6d28d9}
.msg{padding:10px 14px;border-radius:9px;margin-bottom:12px;font-size:13px;background:#2a0e0e;color:#f87171;border:1px solid #4a0d0d}
</style></head><body><div class="login-box card">';
echo '<h1>' . h(APP_NAME) . ' Panel</h1><div class="sub">ورود به پنل اعلان و آپدیت</div>';
if ($err !== '') {
    echo '<div class="msg">' . h($err) . '</div>';
}
echo '<form method="post">' . csrf_field();
echo '<label>رمز عبور</label><input type="password" name="password" autofocus required>';
echo '<button class="btn" type="submit">ورود</button></form></div></body></html>';
