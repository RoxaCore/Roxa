<?php
require_once __DIR__ . '/_layout.php';

sess_start();

// Panel bootstrap has been removed from here. Creating the admin password is
// done only via the command line (tools/create-admin.php) on the server itself;
// so no HTTP request — including scanners that probe /admin/ first — can set a password.
if (!is_file(auth_file())) {
    http_response_code(503);
    echo '<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">';
    echo '<meta name="viewport" content="width=device-width, initial-scale=1">';
    echo '<title>پنل هنوز راه‌اندازی نشده — ' . h(APP_NAME) . '</title>';
    echo '<link href="/fonts/vazirmatn.css" rel="stylesheet">';
    echo '<style>
:root{--bg:#0b0f1a;--card:#141a2a;--line:#1f2937;--tx:#e5e7eb;--mut:#9ca3af}
*{box-sizing:border-box;margin:0;padding:0}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;font-size:14px;display:flex;align-items:center;justify-content:center;min-height:100vh}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:28px;max-width:560px;line-height:2;font-size:13px}
h1{font-size:18px;margin-bottom:8px}
.mut{color:var(--mut)}
code{background:#0d1322;border:1px solid var(--line);border-radius:6px;padding:1px 6px;direction:ltr;display:inline-block}
pre{direction:ltr;text-align:left;background:#0d1322;border:1px solid var(--line);border-radius:8px;padding:12px;overflow:auto;font-size:12px;margin:8px 0}
</style></head><body><div class="card">';
    echo '<h1>پنل راه‌اندازی نشده است</h1>';
    echo '<div class="mut">رمز مدیریت ساخته نشده، و ساختِ آن از راهِ وب حذف شده است تا هیچ درخواستِ HTTP‌ای نتواند حساب بسازد.</div>';
    echo '<div class="mut">روی خودِ سرور اجرا کنید:</div>';
    echo '<pre>php tools/create-admin.php</pre>';
    echo '<div class="mut">بعد از ساختنِ رمز، این صفحه به فرمِ ورود تبدیل می‌شود.</div>';
    echo '</div></body></html>';
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
$dlStats = jread(DATA_DIR . '/dlstats.json', ['total' => 0, 'days' => []]);
$dlToday = (int)(is_array($dlStats['days'][date('Y-m-d')] ?? null)
    ? ($dlStats['days'][date('Y-m-d')]['total'] ?? 0)
    : 0);

page_head('داشبورد', 'dash');

echo '<div class="grid">';
echo '<div class="stat"><div class="k">تعداد نصب</div><div class="v" id="stat-installs">' . $stats['installs'] . '</div></div>';
echo '<div class="stat"><div class="k">آنلاین لحظه‌ای</div><div class="v" id="stat-online"><span class="badge b-live">' . (int)$stats['online'] . ' نفر</span></div></div>';
echo '<div class="stat"><div class="k">کاربر فعال (۲۴ ساعت)</div><div class="v" id="stat-active24">' . $stats['active24'] . '</div></div>';
echo '<div class="stat"><div class="k">کاربر فعال (۷ روز)</div><div class="v" id="stat-active7">' . $stats['active7'] . '</div></div>';
echo '<div class="stat"><div class="k">دانلود نصب‌کننده</div><div class="v">' . (int)$dlStats['total'] . ' <small>امروز ' . $dlToday . '</small></div></div>';
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
    echo '<tr><th>شناسه</th><th>نام دستگاه</th><th>سیستم‌عامل</th><th>منبع</th><th>آخرین فعالیت</th></tr>';
    foreach ($recent as $c) {
        $id = (string)$c['id'];
        $t = strtotime((string)($c['last'] ?? '')) ?: 0;
        $ip = src_short((string)($c['ip'] ?? '') !== '' ? (string)$c['ip'] : (string)($c['ip_last'] ?? ''));
        $badge = ($t > 0 && $rnow - $t <= 1200) ? ' <span class="badge b-live">آنلاین</span>' : '';
        $dev = (string)($c['dev'] ?? '');
        echo '<tr><td><a class="mono" style="background:none;padding:0" href="user.php?id=' . h($id) . '">' . h(substr($id, 0, 8)) . '…</a>' . $badge . '</td>';
        echo '<td>' . ($dev !== '' ? h($dev) : '—') . '</td>';
        echo '<td>' . ((string)($c['os'] ?? '') !== '' ? h($c['os']) : '—') . '</td>';
        echo '<td class="mono" style="background:none;padding:0">' . ($ip !== '' ? h($ip) : '—') . '</td>';        echo '<td style="white-space:nowrap">' . ($t > 0 ? h(date('Y-m-d H:i', $t)) : '—') . '</td></tr>';
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
