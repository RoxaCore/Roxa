<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$store = jread(DATA_DIR . '/audit.json', ['items' => []]);
$items = is_array($store['items'] ?? null) ? $store['items'] : [];
$items = array_reverse($items); // newest first

page_head('لاگ تغییرات', 'aud');

echo '<div class="grid">';
echo '<div class="stat"><div class="k">رویدادهای ثبت‌شده</div><div class="v">' . count($items) . ' <small>از ۵۰۰</small></div></div>';
echo '</div>';

echo '<div class="card"><h2>لاگ تغییرات پنل</h2>';
echo '<div class="hint">هر تغییر مهم — ورود موفق، تغییر رمز، انتشار نسخه، اعلان، حذف گزارش، پشتیبان — با زمان و کاربر ثبت می‌شود. '
    . 'مبدأ به‌صورت کلیدِ یک‌طرفه (همان کلیدِ محدودیتِ ورود) ذخیره می‌شود؛ نشانیِ خام در هیچ‌کجای پنل نگه‌داشته نمی‌شود. '
    . 'بیشترین ۵۰۰ رویداد نگه داشته می‌شود.</div>';

if (!$items) {
    echo '<div class="hint">هنوز رویدادی ثبت نشده است.</div></div>';
    page_foot();
    return;
}

echo '<table><tr><th>زمان</th><th>عمل</th><th>جزئیات</th><th>کاربر</th><th>کلید مبدأ</th></tr>';
foreach ($items as $it) {
    $t = strtotime((string)($it['t'] ?? '')) ?: 0;
    echo '<tr>';
    echo '<td style="white-space:nowrap">' . ($t ? h(date('Y-m-d H:i', $t)) : '—') . '</td>';
    echo '<td><span class="badge b-info">' . h((string)($it['a'] ?? '')) . '</span></td>';
    echo '<td>' . h((string)($it['d'] ?? '')) . '</td>';
    echo '<td>' . h((string)($it['u'] ?? '')) . '</td>';
    echo '<td class="mono" style="background:none;padding:4px 0">' . h((string)($it['k'] ?? '')) . '</td>';
    echo '</tr>';
}
echo '</table></div>';

page_foot();
