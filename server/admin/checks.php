<?php
require_once __DIR__ . '/_layout.php';
require_auth();
require_once __DIR__ . '/_checks.php';

// Health test runs on POST only: four HTTP requests with a 5s cap each would
// otherwise fire on every dashboard visit. The result is parked in the session
// so the redirect after POST shows it exactly once (a refresh does not repeat
// the probes).
$health = null;
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    if ((string)($_POST['action'] ?? '') === 'health') {
        $_SESSION['health'] = health_probes();
    }
    header('Location: checks.php');
    exit;
}
if (isset($_SESSION['health']) && is_array($_SESSION['health'])) {
    $health = $_SESSION['health'];
    unset($_SESSION['health']);
}

$items = checklist_items();
$okCount = 0;
foreach ($items as $it) {
    if ($it['ok']) {
        $okCount++;
    }
}

page_head('چک‌لیست و سلامت', 'chk');

echo '<div class="grid">';
echo '<div class="stat"><div class="k">چک‌لیست انتشار</div><div class="v">' . $okCount . ' <small>از ' . count($items) . '</small></div></div>';
echo '<div class="stat"><div class="k">سرویس‌دهنده</div><div class="v"><small>' . h(BASE_URL) . '</small></div></div>';
echo '</div>';

echo '<div class="card"><h2>چک‌لیست انتشار</h2>';
echo '<div class="hint">بررسی‌های ثابت (فایل‌ها و پرچم‌ها) — بدون تماس با شبکه، هر بار که صفحه باز شود اجرا می‌شود.</div><table>';
echo '<tr><th style="width:30px"></th><th>مورد</th><th>جزئیات</th></tr>';
foreach ($items as $it) {
    echo '<tr>';
    echo '<td>' . ($it['ok'] ? '<span style="color:#34d399">✓</span>' : '<span style="color:#f87171">✗</span>') . '</td>';
    echo '<td>' . h($it['label']) . '</td>';
    echo '<td class="hint" style="margin:0">' . h($it['hint']) . '</td>';
    echo '</tr>';
}
echo '</table></div>';

echo '<div class="card"><h2>تست سلامت endpointها</h2>';
echo '<div class="hint">چهار درخواست واقعی به سرور: صفحهٔ اصلی، API نسخه، API اعلان‌ها و شمارندهٔ دانلود (با نامِ نامعتبر تا شمارشِ جعلی ثبت نشود). پاسخِ مورد انتظار هر کدام کنارش نوشته شده.</div>';
echo '<form method="post">' . csrf_field() . '<input type="hidden" name="action" value="health">';
echo '<button class="btn" type="submit">اجرای تست سلامت</button></form>';
if (is_array($health)) {
    echo '<table style="margin-top:14px">';
    echo '<tr><th>آدرس</th><th>وضعیت</th><th>پاسخ</th><th>زمان</th></tr>';
    foreach ($health as $p) {
        echo '<tr><td class="mono" style="background:none;padding:4px 0">' . h(str_replace(BASE_URL, '', $p['url'])) . '</td>';
        echo '<td><span class="badge ' . ($p['ok'] ? 'b-on">سالم' : 'b-off">خطا') . '</span></td>';
        echo '<td>' . ($p['got'] > 0 ? (int)$p['got'] : '—') . ' <small>(انتظار ' . $p['expect'] . ')</small></td>';
        echo '<td>' . (int)$p['ms'] . ' ms</td></tr>';
    }
    echo '</table>';
    $bad = 0;
    foreach ($health as $p) {
        if (!$p['ok']) {
            $bad++;
        }
    }
    if ($bad === 0) {
        echo '<div class="msg ok" style="margin-top:12px">همهٔ endpointها پاسخِ درست دادند.</div>';
    } else {
        echo '<div class="msg err" style="margin-top:12px">' . $bad . ' مورد پاسخِ درست نداد؛ وضعیت PHP و مسیرها روی سرور را بررسی کنید.</div>';
    }
}
echo '</div>';

page_foot();
