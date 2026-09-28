<?php
require_once __DIR__ . '/../lib.php';

function page_head(string $title, string $active): void
{
    $flash = flash_pop();
    $nav = [
        'dash' => ['داشبورد', 'index.php'],
        'users' => ['کاربران', 'users.php'],
        'ann' => ['اعلان‌ها', 'announcements.php'],
        'ver' => ['نسخه و آپدیت', 'version.php'],
        'pw' => ['تغییر رمز', 'password.php'],
    ];
    $user = '';
    if (isset($_SESSION['username']) && $_SESSION['username'] !== '') {
        $user = h((string)$_SESSION['username']);
    }
    echo '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">';
    echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
    echo '<title>' . h($title) . ' — ' . h(APP_NAME) . ' Panel</title>';
    echo '<link rel="preconnect" href="https://cdn.jsdelivr.net">';
    echo '<link href="https://cdn.jsdelivr.net/gh/rastikerdar/vazirmatn@v33.003/Vazirmatn-font-face.css" rel="stylesheet">';
    echo '<style>
:root{--bg:#0b0f1a;--side:#0e1424;--card:#141a2a;--line:#1f2937;--tx:#e5e7eb;--mut:#9ca3af;--ac:#7c3aed;--ac2:#a78bfa;--ok:#10b981;--er:#ef4444;--wn:#f59e0b}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;font-size:14px;min-height:100vh}
a{color:var(--ac2);text-decoration:none}
.wrap{display:flex;min-height:100vh}
.side{width:230px;background:var(--side);border-left:1px solid var(--line);padding:20px 0;position:fixed;top:0;bottom:0;right:0;display:flex;flex-direction:column}
.brand{padding:0 20px 18px;border-bottom:1px solid var(--line);margin-bottom:12px}
.brand b{font-size:19px;color:#fff}
.brand span{display:block;color:var(--mut);font-size:11px;margin-top:3px}
.nav{flex:1}
.nav a{display:block;padding:11px 20px;color:var(--tx);border-right:3px solid transparent}
.nav a:hover{background:#151c30}
.nav a.on{background:#1a1440;border-right-color:var(--ac);color:#fff}
.side .out{padding:11px 20px;color:var(--er);border-top:1px solid var(--line)}
.main{flex:1;margin-right:230px;padding:26px 30px;max-width:1100px}
.top{display:flex;justify-content:space-between;align-items:center;margin-bottom:22px}
.top h1{font-size:21px}
.top .u{color:var(--mut);font-size:12px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:20px;margin-bottom:18px}
.card h2{font-size:15px;margin-bottom:14px;color:#fff}
.grid{display:grid;gap:14px;grid-template-columns:repeat(auto-fit,minmax(200px,1fr));margin-bottom:18px}
.stat{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px}
.stat .k{color:var(--mut);font-size:12px}
.stat .v{font-size:22px;font-weight:700;margin-top:6px;color:#fff;word-break:break-all}
.stat .v small{font-size:13px;color:var(--ac2);font-weight:400}
label{display:block;color:var(--mut);font-size:12px;margin:12px 0 5px}
input[type=text],input[type=password],input[type=file],textarea,select{width:100%;background:#0d1322;border:1px solid var(--line);color:var(--tx);border-radius:9px;padding:9px 12px;font-family:inherit;font-size:14px}
textarea{min-height:90px;resize:vertical}
input:focus,textarea:focus,select:focus{outline:none;border-color:var(--ac)}
.btn{display:inline-block;background:var(--ac);color:#fff;border:none;border-radius:9px;padding:9px 18px;font-family:inherit;font-size:14px;cursor:pointer;margin-top:14px}
.btn:hover{background:#6d28d9}
.btn.sec{background:#1f2937}
.btn.danger{background:#7f1d1d}
.btn.sm{padding:4px 10px;font-size:12px;margin:0 3px}
.row{display:flex;gap:14px;flex-wrap:wrap}
.row>div{flex:1;min-width:220px}
table{width:100%;border-collapse:collapse}
th,td{text-align:right;padding:9px 8px;border-bottom:1px solid var(--line);font-size:13px;vertical-align:top}
th{color:var(--mut);font-weight:400;font-size:12px}
.badge{display:inline-block;padding:2px 9px;border-radius:20px;font-size:11px}
.b-info{background:#0e2a3a;color:#38bdf8}
.b-warn{background:#3a2a0e;color:#fbbf24}
.b-update{background:#2a1a3a;color:#c084fc}
.b-on{background:#062a1c;color:#34d399}
.b-off{background:#2a0e0e;color:#f87171}
.msg{padding:11px 15px;border-radius:10px;margin-bottom:16px;font-size:13px}
.msg.ok{background:#062a1c;color:#34d399;border:1px solid #0d4a33}
.msg.err{background:#2a0e0e;color:#f87171;border:1px solid #4a0d0d}
.hint{color:var(--mut);font-size:12px;margin-top:8px;line-height:1.9}
.hint code{background:#0d1322;padding:1px 7px;border-radius:5px;color:#c084fc;direction:ltr;display:inline-block}
.check{display:flex;align-items:center;gap:8px;margin-top:12px;color:var(--tx);font-size:13px}
.check input{width:auto}
.login-box{max-width:380px;margin:9vh auto;padding:30px}
.mono{direction:ltr;text-align:left;word-break:break-all;color:var(--mut);font-size:12px;background:#0d1322;padding:8px 10px;border-radius:8px}
@media(max-width:760px){.side{display:none}.main{margin-right:0;padding:16px}}
</style></head><body><div class="wrap">';
    echo '<aside class="side"><div class="brand"><b>' . h(APP_NAME) . '</b><span>Update &amp; Notification Panel</span></div>';
    echo '<nav class="nav">';
    foreach ($nav as $key => $item) {
        $on = $key === $active ? ' class="on"' : '';
        echo '<a href="' . $item[1] . '"' . $on . '>' . h($item[0]) . '</a>';
    }
    echo '</nav>';
    echo '<a class="out" href="logout.php">خروج از حساب</a></aside>';
    echo '<main class="main">';
    echo '<div class="top"><h1>' . h($title) . '</h1>';
    echo '<div class="u">' . ($user !== '' ? 'کاربر: ' . $user . ' · ' : '') . date('Y-m-d H:i') . '</div></div>';
    if ($flash) {
        $cls = $flash['t'] === 'err' ? 'err' : 'ok';
        echo '<div class="msg ' . $cls . '">' . h($flash['m']) . '</div>';
    }
}

function page_foot(): void
{
    echo '</main></div></body></html>';
}
