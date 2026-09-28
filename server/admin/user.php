<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$id = strtolower(trim((string)($_GET['id'] ?? '')));
$c = stats_find($id);

page_head('پروفایل کاربر', 'users');

if ($c === null) {
    echo '<div class="card"><h2>کاربر یافت نشد</h2><div class="hint">شناسه نامعتبر است یا هنوز چنین کاربری ثبت نشده است. <a href="users.php">→ بازگشت به لیست کاربران</a></div></div>';
    page_foot();
    exit;
}

$now = time();
$t = strtotime((string)($c['last'] ?? '')) ?: 0;
$f = strtotime((string)($c['first'] ?? '')) ?: 0;
$status = 'غیرفعال';
$statusCls = 'b-off';
if ($t > 0 && $now - $t <= 86400) {
    $status = 'فعال (۲۴ ساعت)';
    $statusCls = 'b-on';
} elseif ($t > 0 && $now - $t <= 604800) {
    $status = 'فعال (۷ روز)';
    $statusCls = 'b-info';
}

echo '<div class="card"><h2>اطلاعات کاربر <span class="badge ' . $statusCls . '">' . $status . '</span></h2>';
echo '<div class="grid">';
echo '<div class="stat"><div class="k">سیستم‌عامل</div><div class="v"><small>' . ((string)($c['os'] ?? '') !== '' ? h($c['os']) : '—') . '</small></div></div>';
echo '<div class="stat"><div class="k">IP (مستقیم)</div><div class="v"><small>' . ((string)($c['ip'] ?? '') !== '' ? h($c['ip']) : '—') . '</small></div></div>';
echo '<div class="stat"><div class="k">IP آخرین درخواست</div><div class="v"><small>' . ((string)($c['ip_last'] ?? '') !== '' ? h($c['ip_last']) : '—') . '</small></div></div>';
echo '<div class="stat"><div class="k">کشور</div><div class="v">' . ((string)($c['country'] ?? '') !== '' ? h($c['country']) : '—') . '</div></div>';
echo '<div class="stat"><div class="k">زبان برنامه</div><div class="v">' . ((string)($c['lang'] ?? '') !== '' ? h($c['lang']) : '—') . '</div></div>';
echo '<div class="stat"><div class="k">نسخه نصب‌شده</div><div class="v">' . ((string)($c['ver'] ?? '') !== '' ? h($c['ver']) : '—') . '</div></div>';
echo '<div class="stat"><div class="k">آخرین فعالیت</div><div class="v"><small>' . ($t > 0 ? h(date('Y-m-d H:i', $t)) : '—') . '</small></div></div>';
echo '<div class="stat"><div class="k">اولین فعالیت</div><div class="v"><small>' . ($f > 0 ? h(date('Y-m-d H:i', $f)) : '—') . '</small></div></div>';
echo '</div>';
echo '<div class="hint">شناسه: <code style="direction:ltr;display:inline-block">' . h($c['id']) . '</code></div>';
echo '<div class="hint"><a href="users.php">→ بازگشت به لیست کاربران</a></div>';
echo '</div>';

page_foot();
