<?php
require_once __DIR__ . '/_layout.php';

sess_start();

if (!is_file(auth_file())) {
    // راه‌اندازی فقط با کلید SETUP_KEY (config.php)؛ اسکنرهایی که اول /admin/ را
    // می‌بینند دیگر نمی‌توانند رمز مدیر را بسازند.
    $setupKey = defined('SETUP_KEY') ? (string)SETUP_KEY : '';
    $givenKey = (string)($_REQUEST['key'] ?? '');
    if ($setupKey === '' || $givenKey === '' || !hash_equals($setupKey, $givenKey)) {
        http_response_code(403);
        echo '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">';
        echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
        echo '<title>راه‌اندازی بسته — ' . h(APP_NAME) . ' Panel</title>';
        echo '<link href="https://cdn.jsdelivr.net/gh/rastikerdar/vazirmatn@v33.003/Vazirmatn-font-face.css" rel="stylesheet">';
        echo '<style>
:root{--bg:#0b0f1a;--card:#141a2a;--line:#1f2937;--tx:#e5e7eb;--mut:#9ca3af}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;font-size:14px;display:flex;align-items:center;justify-content:center;min-height:100vh}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:28px;max-width:520px;line-height:2;font-size:13px}
h1{font-size:18px;margin-bottom:8px}
.mut{color:var(--mut)}
code{background:#0d1322;border:1px solid var(--line);border-radius:6px;padding:1px 6px;direction:ltr;display:inline-block}
</style></head><body><div class="card">';
        echo '<h1>راه‌اندازی بسته است</h1>';
        if ($setupKey === '') {
            echo '<div class="mut">هنوز <code>auth.json</code> ساخته نشده و کلید راه‌اندازی هم خالی است. برای امنیت، ساخت رمز از راه دور غیرفعال است:</div>';
            echo '<div class="mut">۱) در <code>config.php</code> مقدار <code>SETUP_KEY</code> را یک رشتهٔ تصادفی کنید<br>۲) این صفحه را با <code>admin/index.php?key=رشته</code> باز کنید<br>۳) بعد از ساخت رمز، دوباره آن را خالی کنید.</div>';
        } else {
            echo '<div class="mut">کلید راه‌اندازی نادرست یا خالی است. آدرس را با <code>admin/index.php?key=SETUP_KEY</code> باز کنید.</div>';
        }
        echo '</div></body></html>';
        exit;
    }
    $err = '';
    if ($_SERVER['REQUEST_METHOD'] === 'POST') {
        csrf_check();
        $p1 = (string)($_POST['password'] ?? '');
        $p2 = (string)($_POST['confirm'] ?? '');
        if (strlen($p1) < 8) {
            $err = 'رمز باید حداقل ۸ کاراکتر باشد.';
        } elseif ($p1 !== $p2) {
            $err = 'تکرار رمز مطابقت ندارد.';
        } else {
            jwrite(auth_file(), [
                'hash' => password_hash($p1, PASSWORD_DEFAULT),
                'created' => date('c'),
            ]);
            session_regenerate_id(true);
            $_SESSION['auth'] = true;
            $_SESSION['auth_at'] = time();
            $_SESSION['username'] = 'admin';
            flash('رمز مدیریت ساخته شد. خوش آمدید.');
            header('Location: index.php');
            exit;
        }
    }
    echo '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">';
    echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
    echo '<title>راه‌اندازی — ' . h(APP_NAME) . '</title>';
    echo '<link href="https://cdn.jsdelivr.net/gh/rastikerdar/vazirmatn@v33.003/Vazirmatn-font-face.css" rel="stylesheet">';
    echo '<style>
:root{--bg:#0b0f1a;--card:#141a2a;--line:#1f2937;--tx:#e5e7eb;--mut:#9ca3af;--ac:#7c3aed}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;font-size:14px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:28px}
h1{font-size:20px;margin-bottom:4px}
.sub{color:var(--mut);font-size:12px;margin-bottom:10px}
label{display:block;color:var(--mut);font-size:12px;margin:12px 0 5px}
input[type=password]{width:100%;background:#0d1322;border:1px solid var(--line);color:var(--tx);border-radius:9px;padding:10px 12px;font-family:inherit;font-size:14px}
input:focus{outline:none;border-color:var(--ac)}
.btn{width:100%;background:var(--ac);color:#fff;border:none;border-radius:9px;padding:11px;font-family:inherit;font-size:15px;cursor:pointer;margin-top:18px}
.btn:hover{background:#6d28d9}
.msg{padding:10px 14px;border-radius:9px;margin-bottom:12px;font-size:13px;background:#2a0e0e;color:#f87171;border:1px solid #4a0d0d}
.note{color:var(--mut);font-size:12px;line-height:2;margin-bottom:8px}
</style></head><body><div class="login-box card">';
    echo '<h1>راه‌اندازی پنل</h1><div class="sub">اولین ورود — رمز مدیریت را بسازید</div>';
    echo '<div class="note">این رمز فقط همین‌جا (هاست شما) ذخیره می‌شود و به هیچ سرور دیگری نمی‌رود. حتماً آن را نگه دارید.</div>';
    if ($err !== '') {
        echo '<div class="msg">' . h($err) . '</div>';
    }
    echo '<form method="post">' . csrf_field();
    echo '<label>رمز عبور (حداقل ۸ کاراکتر)</label><input type="password" name="password" required autofocus>';
    echo '<label>تکرار رمز</label><input type="password" name="confirm" required>';
    echo '<button class="btn" type="submit">ساخت رمز و ورود</button></form></div></body></html>';
    exit;
}

require_auth();

$ver = jread(DATA_DIR . '/version.json', ['latest' => null, 'active' => false]);
$ann = jread(DATA_DIR . '/announcements.json', ['seq' => 0, 'items' => []]);
$activeCount = 0;
foreach (($ann['items'] ?? []) as $it) {
    if (!empty($it['active'])) {
        $activeCount++;
    }
}
$rep = jread(DATA_DIR . '/reports.json', ['seq' => 0, 'items' => []]);
$newReports = 0;
foreach (($rep['items'] ?? []) as $r) {
    if (empty($r['seen'])) {
        $newReports++;
    }
}
$dataWritable = is_writable(DATA_DIR);
$dlWritable = is_writable(DL_DIR);
$stats = stats_summary();

page_head('داشبورد', 'dash');

echo '<div class="grid">';
echo '<div class="stat"><div class="k">تعداد نصب</div><div class="v" id="stat-installs">' . $stats['installs'] . '</div></div>';
echo '<div class="stat"><div class="k">آنلاین لحظه‌ای</div><div class="v" id="stat-online"><span class="badge b-live">' . (int)$stats['online'] . ' نفر</span></div></div>';
echo '<div class="stat"><div class="k">کاربر فعال (۲۴ ساعت)</div><div class="v" id="stat-active24">' . $stats['active24'] . '</div></div>';
echo '<div class="stat"><div class="k">کاربر فعال (۷ روز)</div><div class="v" id="stat-active7">' . $stats['active7'] . '</div></div>';
echo '<div class="stat"><div class="k">نسخه منتشرشده</div><div class="v">' . h((string)($ver['latest'] ?: '—')) . '</div></div>';
echo '<div class="stat"><div class="k">وضعیت انتشار</div><div class="v"><span class="badge ' . (!empty($ver['active']) ? 'b-on">فعال' : 'b-off">غیرفعال') . '</span></div></div>';
echo '<div class="stat"><div class="k">اعلان‌های فعال</div><div class="v">' . $activeCount . ' <small>از ' . count($ann['items'] ?? []) . '</small></div></div>';
echo '<div class="stat"><div class="k">گزارش‌های جدید</div><div class="v">' . ($newReports > 0 ? '<a href="reports.php" style="color:inherit">' . $newReports . ' <small>گزارش</small></a>' : '0') . '</div></div>';
echo '<div class="stat"><div class="k">نسخهٔ PHP</div><div class="v"><small>' . h(PHP_VERSION) . '</small></div></div>';
echo '</div>';

$recent = array_slice(stats_sorted(), 0, 10);
if ($recent) {
    $rnow = time();
    echo '<div class="card"><h2>کاربران اخیر <a href="users.php" style="font-size:12px;font-weight:400">→ مشاهده همه</a></h2><table>';
    echo '<tr><th>شناسه</th><th>نام دستگاه</th><th>سیستم‌عامل</th><th>IP</th><th>آخرین فعالیت</th></tr>';
    foreach ($recent as $c) {
        $id = (string)$c['id'];
        $t = strtotime((string)($c['last'] ?? '')) ?: 0;
        $ip = (string)($c['ip'] ?? '') !== '' ? (string)$c['ip'] : (string)($c['ip_last'] ?? '');
        $badge = ($t > 0 && $rnow - $t <= 1200) ? ' <span class="badge b-live">آنلاین</span>' : '';
        $dev = (string)($c['dev'] ?? '');
        echo '<tr><td><a class="mono" style="background:none;padding:0" href="user.php?id=' . h($id) . '">' . h(substr($id, 0, 8)) . '…</a>' . $badge . '</td>';
        echo '<td>' . ($dev !== '' ? h($dev) : '—') . '</td>';
        echo '<td>' . ((string)($c['os'] ?? '') !== '' ? h($c['os']) : '—') . '</td>';
        echo '<td class="mono" style="background:none;padding:0">' . ($ip !== '' ? h($ip) : '—') . '</td>';
        echo '<td style="white-space:nowrap">' . ($t > 0 ? h(date('Y-m-d H:i', $t)) : '—') . '</td></tr>';
    }
    echo '</table></div>';
}

echo '<div class="card"><h2>وضعیت سرویس</h2>';
echo '<div class="hint">';
echo 'آدرس API نسخه: <code>' . h(BASE_URL) . '/api/check.php</code><br>';
echo 'آدرس API اعلان‌ها: <code>' . h(BASE_URL) . '/api/announcements.php</code><br>';
echo 'پوشهٔ داده‌ها (data): ' . ($dataWritable ? '<span style="color:#34d399">قابل نوشتن ✓</span>' : '<span style="color:#f87171">غیرقابل نوشتن ✗</span>') . ' — ';
echo 'پوشهٔ دانلود (dl): ' . ($dlWritable ? '<span style="color:#34d399">قابل نوشتن ✓</span>' : '<span style="color:#f87171">غیرقابل نوشتن ✗</span>');
echo '</div></div>';

echo '<div class="card"><h2>دسترسی سریع</h2><div class="hint">';
echo '<a href="announcements.php">→ نوشتن اعلان جدید</a><br>';
echo '<a href="version.php">→ انتشار نسخه جدید آپدیت</a><br>';
echo '<a href="users.php">→ لیست کاربران و پروفایل‌ها</a><br>';
echo '<a href="reports.php">→ گزارش‌های باگ و مشکل کاربران</a><br>';
echo '<a href="../api/check.php" target="_blank">→ مشاهدهٔ پاسخ API نسخه</a> · ';
echo '<a href="../api/announcements.php" target="_blank">→ پاسخ API اعلان‌ها</a>';
echo '</div></div>';

page_foot();
