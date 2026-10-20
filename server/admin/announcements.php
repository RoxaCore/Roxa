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
        // Only https is accepted: `javascript:`, `data:` and `http:` are rejected.
        $url = safe_url(trim((string)($_POST['url'] ?? '')));
        $langs = langs_field();
        $countries = country_field();
        $active = isset($_POST['active']) ? true : false;
        if ($title === '' || $body === '') {
            flash('عنوان و متن اعلان اجباری است.', 'err');
        } elseif ($url === null) {
            flash('لینک باید با https:// شروع شود؛ لینک پذیرفته نشد.', 'err');
        } else {
            $seq = 0;
            jupdate(DATA_DIR . '/announcements.json', function (array $store) use ($title, $body, $level, $url, $langs, $countries, $active, &$seq) {
                $store['seq'] = (int)($store['seq'] ?? 0) + 1;
                $seq = (int)$store['seq'];
                if (!is_array($store['items'] ?? null)) {
                    $store['items'] = [];
                }
                $store['items'][] = [
                    'id' => $seq,
                    'title' => str_cut($title, 120),
                    'body' => str_cut($body, 2000),
                    'level' => $level,
                    'url' => str_cut($url, 300),
                    'langs' => $langs,
                    'countries' => $countries,
                    'active' => $active,
                    'created' => date('c'),
                ];
                return $store;
            });
            flash('اعلان ثبت شد.');
        }
    } elseif ($action === 'toggle') {
        $id = (int)($_POST['id'] ?? 0);
        $found = false;
        jupdate(DATA_DIR . '/announcements.json', function (array $store) use ($id, &$found) {
            if (!is_array($store['items'] ?? null)) {
                $store['items'] = [];
                return $store;
            }
            foreach ($store['items'] as $k => $it) {
                if ((int)($it['id'] ?? 0) === $id) {
                    $store['items'][$k]['active'] = empty($it['active']);
                    $found = true;
                    break;
                }
            }
            return $store;
        });
        flash($found ? 'وضعیت اعلان عوض شد.' : 'اعلان پیدا نشد.', $found ? 'ok' : 'err');
    } elseif ($action === 'delete') {
        $id = (int)($_POST['id'] ?? 0);
        $found = false;
        jupdate(DATA_DIR . '/announcements.json', function (array $store) use ($id, &$found) {
            if (!is_array($store['items'] ?? null)) {
                $store['items'] = [];
                return $store;
            }
            $new = [];
            foreach ($store['items'] as $it) {
                if ((int)($it['id'] ?? 0) !== $id) {
                    $new[] = $it;
                } elseif (!$found) {
                    $found = true;
                }
            }
            $store['items'] = $new;
            return $store;
        });
        flash($found ? 'اعلان حذف شد.' : 'اعلان پیدا نشد.', $found ? 'ok' : 'err');
    }
    header('Location: announcements.php');
    exit;
}

$levels = ['info' => ['اطلاع‌رسانی', 'b-info'], 'warn' => ['هشدار', 'b-warn'], 'update' => ['آپدیت', 'b-update']];

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
echo '<div><label>کشورهای هدف (اختیاری)</label><input type="text" name="countries" placeholder="IR,TR — خالی = همه"></div></div>';
echo '<div class="hint">کدهای ISO دوحرفه‌ای که اعلان فقط به کاربران آن‌ها نمایش داده شود؛ خالی = همه کشورها.</div>';
echo lang_picker();
echo '<label>متن</label><textarea name="body" maxlength="2000" required></textarea>';
echo '<label>لینک (اختیاری)</label><input type="text" name="url" placeholder="https://...">';
echo '<div class="check"><input type="checkbox" name="active" id="active" checked><label for="active" style="margin:0">فعال (برای کاربران نمایش داده شود)</label></div>';
echo '<button class="btn" type="submit">ثبت اعلان</button></form></div>';

$items = array_reverse($store['items'] ?? []);
if (!$items) {
    echo '<div class="card"><div class="hint">هنوز اعلانی ثبت نشده است.</div></div>';
} else {
    echo '<div class="card"><h2>لیست اعلان‌ها (' . count($items) . ')</h2><table>';
    echo '<tr><th>#</th><th>عنوان</th><th>متن</th><th>سطح</th><th>زبان</th><th>کشورها</th><th>تاریخ</th><th>وضعیت</th><th>عملیات</th></tr>';
    foreach ($items as $it) {
        $lv = $levels[$it['level']] ?? $levels['info'];
        $on = !empty($it['active']);
        $ics = (string)($it['countries'] ?? '');
        $il = item_langs($it);
        echo '<tr><td>' . (int)$it['id'] . '</td>';
        echo '<td>' . h($it['title']) . ((string)($it['url'] ?? '') !== '' && safe_url((string)$it['url']) !== null ? ' <a href="' . h((string)$it['url']) . '" target="_blank" rel="noopener noreferrer">🔗</a>' : '') . '</td>';
        echo '<td style="max-width:280px;color:#9ca3af">' . h(str_cut((string)$it['body'], 90)) . (str_len((string)$it['body']) > 90 ? '…' : '') . '</td>';
        echo '<td><span class="badge ' . $lv[1] . '">' . h($lv[0]) . '</span></td>';
        echo '<td>' . ($il ? h(implode(',', $il)) : '<span style="color:#9ca3af">همه</span>') . '</td>';
        echo '<td>' . ($ics !== '' ? h($ics) : '<span style="color:#9ca3af">همه</span>') . '</td>';
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
