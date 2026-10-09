<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$store = jread(DATA_DIR . '/reports.json', ['seq' => 0, 'items' => []]);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = (string)($_POST['action'] ?? '');
    if ($action === 'seen') {
        $n = (int)($_POST['n'] ?? 0);
        foreach ($store['items'] as $k => $it) {
            if ((int)($it['n'] ?? 0) === $n) {
                $store['items'][$k]['seen'] = true;
                break;
            }
        }
        jwrite(DATA_DIR . '/reports.json', $store);
        flash('گزارش خوانده شد.');
    } elseif ($action === 'seenall') {
        foreach ($store['items'] as $k => $it) {
            $store['items'][$k]['seen'] = true;
        }
        jwrite(DATA_DIR . '/reports.json', $store);
        flash('همه گزارش‌ها خوانده شد.');
    } elseif ($action === 'delete') {
        $n = (int)($_POST['n'] ?? 0);
        $new = [];
        foreach ($store['items'] as $it) {
            if ((int)($it['n'] ?? 0) !== $n) {
                $new[] = $it;
            }
        }
        $store['items'] = $new;
        jwrite(DATA_DIR . '/reports.json', $store);
        flash('گزارش حذف شد.');
    } elseif ($action === 'deleteall') {
        $store['items'] = [];
        jwrite(DATA_DIR . '/reports.json', $store);
        flash('همه گزارش‌ها حذف شد.');
    }
    header('Location: reports.php');
    exit;
}

$items = array_reverse($store['items'] ?? []);
$newCount = 0;
foreach (($store['items'] ?? []) as $it) {
    if (empty($it['seen'])) {
        $newCount++;
    }
}

page_head('گزارش‌ها', 'rep');

echo '<div class="card"><h2>گزارش باگ و مشکل کاربران';
if ($newCount > 0) {
    echo ' <span class="badge b-warn">' . $newCount . ' جدید</span>';
}
echo '</h2><div class="hint">گزارش‌ها از دو منبع می‌آیند: <b>کرش</b> (به‌صورت خودکار هنگام خرابی برنامه، در اجرای بعدی کاربر ارسال می‌شود) و <b>دستی</b> (کاربر از بخش «گزارش مشکل» در تنظیمات). هر گزارش شامل متن کاربر به‌علاوهٔ اطلاعات خودکار برنامه (نسخه، سیستم‌عامل، شناسه دستگاه، خطا و لاگ) است.</div>';
echo '<div class="row">';
if ($newCount > 0) {
    echo '<form method="post" style="display:inline">' . csrf_field() . '<input type="hidden" name="action" value="seenall"><button class="btn sec sm" type="submit">علامت‌گذاری همه خوانده‌شده</button></form> ';
}
if ($items) {
    echo '<form method="post" style="display:inline" onsubmit="return confirm(\'همه گزارش‌ها حذف شود؟\')">' . csrf_field() . '<input type="hidden" name="action" value="deleteall"><button class="btn danger sm" type="submit">حذف همه</button></form>';
}
echo '</div></div>';

if (!$items) {
    echo '<div class="card"><div class="hint">هنوز گزارشی ثبت نشده است.</div></div>';
} else {
    echo '<div class="card"><h2>لیست گزارش‌ها (' . count($items) . ')</h2><table>';
    echo '<tr><th>#</th><th>نوع</th><th>کاربر</th><th>مشکل</th><th>تاریخ</th><th>وضعیت</th><th>عملیات</th></tr>';
    foreach ($items as $it) {
        $n = (int)($it['n'] ?? 0);
        $isCrash = (string)($it['type'] ?? '') === 'crash';
        $seen = !empty($it['seen']);
        $id = (string)($it['id'] ?? '');
        $msg = (string)($it['message'] ?? '');
        $err = (string)($it['error'] ?? '');
        $log = (string)($it['log'] ?? '');

        echo '<tr' . ($seen ? '' : ' style="background:#171233"') . '><td>' . $n . '</td>';
        echo '<td><span class="badge ' . ($isCrash ? 'b-er' : 'b-info') . '">' . ($isCrash ? 'کرش' : 'دستی') . '</span></td>';
        echo '<td style="white-space:nowrap">';
        if ($id !== '') {
            echo '<a class="mono" href="user.php?id=' . h($id) . '">' . h(substr($id, 0, 8)) . '…</a><br>';
        }
        echo '<small>' . h((string)($it['ver'] ?? '')) . '</small>';
        if ((string)($it['os'] ?? '') !== '') {
            echo '<br><small style="color:#9ca3af">' . h(str_cut((string)$it['os'], 40)) . '</small>';
        }
        if ((string)($it['ip'] ?? '') !== '') {
            echo '<br><small class="mono" style="color:#6b7280">' . h((string)$it['ip']) . '</small>';
        }
        echo '</td>';

        echo '<td style="max-width:420px">';
        if ($msg !== '') {
            echo '<div style="color:#e5e7eb">' . h(str_cut($msg, 200)) . (str_len($msg) > 200 ? '…' : '') . '</div>';
        }
        if ($err !== '') {
            echo '<details><summary style="color:#f87171;cursor:pointer">جزئیات خطا</summary>';
            echo '<pre style="white-space:pre-wrap;word-break:break-all;font-size:11px;color:#9ca3af;max-height:240px;overflow:auto">' . h($err) . '</pre></details>';
        }
        if ($log !== '') {
            echo '<details><summary style="color:#9ca3af;cursor:pointer">لاگ اخیر</summary>';
            echo '<pre style="white-space:pre-wrap;font-size:11px;color:#9ca3af;max-height:240px;overflow:auto">' . h($log) . '</pre></details>';
        }
        if ($msg === '' && $err === '' && $log === '') {
            echo '<span style="color:#9ca3af">(خالی)</span>';
        }
        echo '</td>';

        echo '<td style="white-space:nowrap">' . h(date('Y-m-d H:i', strtotime((string)($it['at'] ?? ''))) ?: '—') . '</td>';
        echo '<td><span class="badge ' . ($seen ? 'b-on">خوانده' : 'b-off">جدید') . '</span></td>';
        echo '<td style="white-space:nowrap">';
        if (!$seen) {
            echo '<form method="post" style="display:inline">' . csrf_field() . '<input type="hidden" name="action" value="seen"><input type="hidden" name="n" value="' . $n . '"><button class="btn sec sm" type="submit">خوانده شد</button></form> ';
        }
        echo '<form method="post" style="display:inline" onsubmit="return confirm(\'حذف شود؟\')">' . csrf_field() . '<input type="hidden" name="action" value="delete"><input type="hidden" name="n" value="' . $n . '"><button class="btn danger sm" type="submit">حذف</button></form>';
        echo '</td></tr>';
    }
    echo '</table></div>';
}

page_foot();
