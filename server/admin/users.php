<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$all = stats_sorted();
$total = count($all);
$limit = 200;
$now = time();

// Filters (GET): free-text search, activity window, exact version.
$q = trim((string)($_GET['q'] ?? ''));
$fst = (string)($_GET['st'] ?? 'all');
if (!in_array($fst, ['all', 'online', 'a24', 'a7', 'stale'], true)) {
    $fst = 'all';
}
$fver = trim((string)($_GET['ver'] ?? ''));

$match = function (array $c) use ($q, $fst, $fver, $now): bool {
    if ($fver !== '' && trim((string)($c['ver'] ?? '')) !== $fver) {
        return false;
    }
    $t = strtotime((string)($c['last'] ?? '')) ?: 0;
    $age = $t > 0 ? $now - $t : PHP_INT_MAX;
    if ($fst === 'online' && $age > 1200) {
        return false;
    }
    if ($fst === 'a24' && $age > 86400) {
        return false;
    }
    if ($fst === 'a7' && $age > 604800) {
        return false;
    }
    if ($fst === 'stale' && $age <= 30 * 86400) {
        return false;
    }
    if ($q !== '') {
        $hay = strtolower(implode(' ', [
            (string)$c['id'],
            (string)($c['dev'] ?? ''),
            (string)($c['os'] ?? ''),
            (string)($c['isp'] ?? ''),
            (string)($c['country'] ?? ''),
            (string)($c['ver'] ?? ''),
        ]));
        if (strpos($hay, strtolower($q)) === false) {
            return false;
        }
    }
    return true;
};
$filtered = array_values(array_filter($all, $match));
$matched = count($filtered);

// Distinct versions for the filter dropdown, newest first.
$verOpts = [];
foreach ($all as $c) {
    $v = trim((string)($c['ver'] ?? ''));
    if ($v !== '') {
        $verOpts[$v] = true;
    }
}
$verOpts = array_keys($verOpts);
usort($verOpts, function ($a, $b) {
    return version_compare($b, $a);
});

// CSV export: every match, not just the visible slice. Runs before page_head
// so nothing but the file reaches the output.
if (isset($_GET['csv'])) {
    header('Content-Type: text/csv; charset=utf-8');
    header('Content-Disposition: attachment; filename="roxa-users-' . date('Y-m-d') . '.csv"');
    header('Cache-Control: no-store');
    $out = fopen('php://output', 'w');
    fwrite($out, "\xEF\xBB\xBF"); // BOM so Excel opens Persian text correctly
    $head = ['id', 'device', 'os', 'country', 'isp', 'version', 'connections', 'first', 'last'];
    fputcsv($out, $head, ',', '"', '');
    foreach ($filtered as $c) {
        fputcsv($out, [
            (string)$c['id'],
            (string)($c['dev'] ?? ''),
            (string)($c['os'] ?? ''),
            (string)($c['country'] ?? ''),
            (string)($c['isp'] ?? ''),
            (string)($c['ver'] ?? ''),
            max(0, (int)($c['conn'] ?? 0)),
            (string)($c['first'] ?? ''),
            (string)($c['last'] ?? ''),
        ], ',', '"', '');
    }
    fclose($out);
    exit;
}

$rows = array_slice($filtered, 0, $limit);

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

// ---- search / filter bar (GET, links stay shareable) ----
$qsCsv = http_build_query(['q' => $q, 'st' => $fst, 'ver' => $fver, 'csv' => 1]);
$stLabels = ['all' => 'همه', 'online' => 'آنلاین (۲۰ دقیقه)', 'a24' => 'فعال (۲۴ ساعت)', 'a7' => 'فعال (۷ روز)', 'stale' => 'غیرفعال بیش از ۳۰ روز'];
echo '<div class="card"><h2>جستجو و فیلتر</h2>';
echo '<form method="get"><div class="row">';
echo '<div><label>جستجو (شناسه، دستگاه، سیستم‌عامل، ISP، کشور، نسخه)</label><input type="text" name="q" value="' . h($q) . '"></div>';
echo '<div><label>وضعیت</label><select name="st">';
foreach ($stLabels as $k => $lb) {
    echo '<option value="' . $k . '"' . ($fst === $k ? ' selected' : '') . '>' . h($lb) . '</option>';
}
echo '</select></div>';
echo '<div><label>نسخه</label><select name="ver"><option value="">همه</option>';
foreach ($verOpts as $v) {
    echo '<option value="' . h($v) . '"' . ($fver === $v ? ' selected' : '') . '>' . h($v) . '</option>';
}
echo '</select></div>';
echo '<div><button class="btn" type="submit" style="margin-top:26px">اعمال فیلتر</button> ';
echo '<a class="btn sec" style="margin-top:26px" href="users.php?' . h($qsCsv) . '">خروجی CSV</a></div>';
echo '</div></form>';
echo '<div class="hint">جستجو در همهٔ فیلدهاست و حروف کوچک/بزرگ فرقی ندارد. CSV همهٔ مطابق‌ها را (بدون سقفِ ۲۰۰ ردیفِ صفحه) خروجی می‌گیرد';
if ($q !== '' || $fst !== 'all' || $fver !== '') {
    echo ' — <a href="users.php">حذفِ فیلترها</a>';
}
echo '.</div></div>';

if (!$total) {
    echo '<div class="card"><div class="hint">هنوز کاربری ثبت نشده است. آمار از لحظه‌ای که اپ کاربران این نسخه را اجرا کند ثبت می‌شود.</div></div>';
} elseif (!$matched) {
    echo '<div class="card"><div class="hint">موردی با این فیلتر پیدا نشد (از ' . $total . ' کل). <a href="users.php">همه را نشان بده</a></div></div>';
} else {
    echo '<div class="card"><h2>لیست کاربران (' . $matched . ($matched !== $total ? ' از ' . $total : '') . ($matched > $limit ? ' — نمایش ' . $limit . ' مورد اول' : '') . ')</h2><table>';
    echo '<tr><th>شناسه</th><th>نام دستگاه</th><th>سیستم‌عامل</th><th>منبع</th><th>ISP</th><th>کشور</th><th>نسخه</th><th>اتصال‌ها</th><th>آخرین فعالیت</th></tr>';
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
        echo '<td class="mono" style="background:none;padding:0">' . (src_short((string)($c['ip'] ?? '')) !== '' ? h(src_short((string)$c['ip'])) : (src_short((string)($c['ip_last'] ?? '')) !== '' ? h(src_short((string)$c['ip_last'])) : '—')) . '</td>';
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
