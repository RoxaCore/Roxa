<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$store = jread(DATA_DIR . '/announcements.json', ['seq' => 0, 'items' => []]);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $action = (string)($_POST['action'] ?? '');
    if ($action === 'add') {
        $title = trim((string)($_POST['title'] ?? ''));
        $body = trim((string)($_POST['body'] ?? ''));
        $level = in_array($_POST['level'] ?? '', ['info', 'warn', 'update'], true) ? $_POST['level'] : 'info';
        $url = trim((string)($_POST['url'] ?? ''));
        $lang = ($_POST['lang'] ?? '*') === '*' ? '*' : lang_ok((string)$_POST['lang']);
        $active = isset($_POST['active']) ? true : false;
        if ($title === '' || $body === '') {
            flash('عنوان و متن اعلان اجباری است.', 'err');
        } else {
            $store['seq'] = (int)($store['seq'] ?? 0) + 1;
            $store['items'][] = [
                'id' => (int)$store['seq'],
                'title' => str_cut($title, 120),
                'body' => str_cut($body, 2000),
                'level' => $level,
                'url' => str_cut($url, 300),
                'lang' => $lang,
                'active' => $active,
                'created' => date('c'),
            ];
            jwrite(DATA_DIR . '/announcements.json', $store);
            flash('اعلان ثبت شد.');
        }
    } elseif ($action === 'toggle') {
        $id = (int)($_POST['id'] ?? 0);
        foreach ($store['items'] as $k => $it) {
            if ((int)$it['id'] === $id) {
                $store['items'][$k]['active'] = empty($it['active']);
                jwrite(DATA_DIR . '/announcements.json', $store);
                flash('وضعیت اعلان عوض شد.');
                break;
            }
        }
    } elseif ($action === 'delete') {
        $id = (int)($_POST['id'] ?? 0);
        $new = [];
        foreach ($store['items'] as $it) {
            if ((int)$it['id'] !== $id) {
                $new[] = $it;
            }
        }
        $store['items'] = $new;
        jwrite(DATA_DIR . '/announcements.json', $store);
        flash('اعلان حذف شد.');
    }
    header('Location: announcements.php');
    exit;
}

$levels = ['info' => ['اطلاع‌رسانی', 'b-info'], 'warn' => ['هشدار', 'b-warn'], 'update' => ['آپدیت', 'b-update']];
$langs = ['*' => 'همه', 'fa' => 'فارسی', 'en' => 'انگلیسی'];

page_head('اعلان‌ها', 'ann');

echo '<div class="card"><h2>اعلان جدید</h2>';
echo '<form method="post">' . csrf_field();
echo '<input type="hidden" name="action" value="add">';
echo '<div class="row"><div><label>عنوان</label><input type="text" name="title" maxlength="120" required></div>';
echo '<div><label>سطح</label><select name="level">';
foreach ($levels as $k => $v) {
    echo '<option value="' . $k . '">' . h($v[0]) . '</option>';
}
echo '</select></div>';
echo '<div><label>زبان</label><select name="lang">';
foreach ($langs as $k => $v) {
    echo '<option value="' . $k . '">' . h($v) . '</option>';
}
echo '</select></div></div>';
echo '<label>متن</label><textarea name="body" maxlength="2000" required></textarea>';
echo '<label>لینک (اختیاری)</label><input type="text" name="url" placeholder="https://...">';
echo '<div class="check"><input type="checkbox" name="active" id="active" checked><label for="active" style="margin:0">فعال (برای کاربران نمایش داده شود)</label></div>';
echo '<button class="btn" type="submit">ثبت اعلان</button></form></div>';

$items = array_reverse($store['items'] ?? []);
if (!$items) {
    echo '<div class="card"><div class="hint">هنوز اعلانی ثبت نشده است.</div></div>';
} else {
    echo '<div class="card"><h2>لیست اعلان‌ها (' . count($items) . ')</h2><table>';
    echo '<tr><th>#</th><th>عنوان</th><th>متن</th><th>سطح</th><th>زبان</th><th>تاریخ</th><th>وضعیت</th><th>عملیات</th></tr>';
    foreach ($items as $it) {
        $lv = $levels[$it['level']] ?? $levels['info'];
        $on = !empty($it['active']);
        echo '<tr><td>' . (int)$it['id'] . '</td>';
        echo '<td>' . h($it['title']) . ($it['url'] !== '' ? ' <a href="' . h($it['url']) . '" target="_blank">🔗</a>' : '') . '</td>';
        echo '<td style="max-width:280px;color:#9ca3af">' . h(str_cut((string)$it['body'], 90)) . (str_len((string)$it['body']) > 90 ? '…' : '') . '</td>';
        echo '<td><span class="badge ' . $lv[1] . '">' . h($lv[0]) . '</span></td>';
        echo '<td>' . h($langs[$it['lang']] ?? $it['lang']) . '</td>';
        echo '<td style="white-space:nowrap">' . h(date('Y-m-d H:i', strtotime((string)$it['created']))) . '</td>';
        echo '<td><span class="badge ' . ($on ? 'b-on">فعال' : 'b-off">غیرفعال') . '</span></td>';
        echo '<td style="white-space:nowrap">';
        echo '<form method="post" style="display:inline">' . csrf_field() . '<input type="hidden" name="action" value="toggle"><input type="hidden" name="id" value="' . (int)$it['id'] . '"><button class="btn sec sm" type="submit">' . ($on ? 'غیرفعال' : 'فعال') . '</button></form>';
        echo '<form method="post" style="display:inline" onsubmit="return confirm(\'حذف شود؟\')">' . csrf_field() . '<input type="hidden" name="action" value="delete"><input type="hidden" name="id" value="' . (int)$it['id'] . '"><button class="btn danger sm" type="submit">حذف</button></form>';
        echo '</td></tr>';
    }
    echo '</table></div>';
}

echo '<div class="card"><h2>نکته</h2><div class="hint">اعلان‌های فعال از آدرس <code>' . h(BASE_URL) . '/api/announcements.php</code> برای اپ ارسال می‌شوند. اپ با شناسهٔ آخرین اعلان دیده‌شده، فقط موارد جدید را نمایش می‌دهد.</div></div>';

page_foot();
