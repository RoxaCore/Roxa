<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$all = stats_sorted();
$total = count($all);
$limit = 200;
$rows = array_slice($all, 0, $limit);
$now = time();

ip_fill($rows, 15, 5.0);

page_head('کاربران', 'users');

$a24 = 0;
$a7 = 0;
$online = 0;
$conns = 0;
foreach ($all as $c) {
    $conns += max(0, (int)($c['conn'] ?? 0));
    $t = strtotime((string)($c['last'] ?? '')) ?: 0;
    if ($t > 0 && $now - $t <= 1200) {
        $online++;
    }
    if ($t > 0 && $now - $t <= 86400) {
        $a24++;
    }
    if ($t > 0 && $now - $t <= 604800) {
        $a7++;
    }
}

echo '<div class="grid">';
echo '<div class="stat"><div class="k">تعداد نصب</div><div class="v">' . $total . '</div></div>';
echo '<div class="stat"><div class="k">آنلاین لحظه‌ای</div><div class="v"><span class="badge b-live">' . $online . ' نفر</span></div></div>';
echo '<div class="stat"><div class="k">فعال (۲۴ ساعت)</div><div class="v">' . $a24 . '</div></div>';
echo '<div class="stat"><div class="k">فعال (۷ روز)</div><div class="v">' . $a7 . '</div></div>';
echo '<div class="stat"><div class="k">مجموع اتصال‌ها</div><div class="v">' . $conns . '</div></div>';
echo '</div>';

if (!$total) {
    echo '<div class="card"><div class="hint">هنوز کاربری ثبت نشده است. آمار از لحظه‌ای که اپ کاربران این نسخه را اجرا کند ثبت می‌شود.</div></div>';
} else {
    echo '<div class="card"><h2>لیست کاربران (' . $total . ($total > $limit ? ' — نمایش ' . $limit . ' مورد اخیر' : '') . ')</h2><table>';
    echo '<tr><th>شناسه</th><th>نام دستگاه</th><th>سیستم‌عامل</th><th>IP</th><th>ISP</th><th>کشور</th><th>نسخه</th><th>اتصال‌ها</th><th>آخرین فعالیت</th></tr>';
    foreach ($rows as $c) {
        $id = (string)$c['id'];
        $t = strtotime((string)($c['last'] ?? '')) ?: 0;
        $badge = '';
        if ($t > 0 && $now - $t <= 1200) {
            $badge = ' <span class="badge b-live">آنلاین</span>';
        } elseif ($t > 0 && $now - $t <= 86400) {
            $badge = ' <span class="badge b-on">فعال</span>';
        }
        $dev = (string)($c['dev'] ?? '');
        $isp = (string)($c['isp'] ?? '');
        $conn = max(0, (int)($c['conn'] ?? 0));
        echo '<tr>';
        echo '<td><a class="mono" style="background:none;padding:0" href="user.php?id=' . h($id) . '">' . h(substr($id, 0, 8)) . '…</a>' . $badge . '</td>';
        echo '<td>' . ($dev !== '' ? h($dev) : '—') . '</td>';
        echo '<td>' . ((string)($c['os'] ?? '') !== '' ? h($c['os']) : '—') . '</td>';
        echo '<td class="mono" style="background:none;padding:0">' . ((string)($c['ip'] ?? '') !== '' ? h($c['ip']) : ((string)($c['ip_last'] ?? '') !== '' ? h($c['ip_last']) : '—')) . '</td>';
        echo '<td>' . ($isp !== '' ? h($isp) : '—') . '</td>';
        echo '<td>' . ((string)($c['country'] ?? '') !== '' ? h($c['country']) : '—') . '</td>';
        echo '<td>' . ((string)($c['ver'] ?? '') !== '' ? h($c['ver']) : '—') . '</td>';
        echo '<td>' . ($conn > 0 ? (string)$conn : '—') . '</td>';
        echo '<td style="white-space:nowrap">' . ($t > 0 ? h(date('Y-m-d H:i', $t)) : '—') . '</td>';
        echo '</tr>';
    }
    echo '</table></div>';
}

echo '<div class="card"><h2>نکته</h2><div class="hint">روی شناسه هر کاربر بزنید تا پروفایل کامل (شناسهٔ دستگاه، IP، ISP/ASN، سیستم‌عامل، کشور، زبان، نسخه، تعداد اتصال و زمان فعالیت) را ببینید. نشان سبز چشمک‌زن «آنلاین» یعنی در ۲۰ دقیقه اخیر گزارش فرستاده است (کلاینت هر ۶۰ دقیقه یک‌بار). نام رایانه دیگر ارسال نمی‌شود و جای آن «شناسه» یکتای ساخته‌شده توسط خود برنامه نمایش داده می‌شود. ISP/ASN از روی IP مستقیم کاربر با ipwho.is پر می‌شود و فقط یک‌بار انجام می‌گیرد. «IP» آدرس مستقیم کاربر است و «IP آخرین درخواست» ممکن است مربوط به گره خروجی VPN باشد.</div></div>';

page_foot();
