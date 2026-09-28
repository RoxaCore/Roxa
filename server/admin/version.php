<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$storeFile = DATA_DIR . '/version.json';
$ver = jread($storeFile, [
    'latest' => null,
    'notes' => '',
    'sha256' => '',
    'force' => false,
    'active' => false,
    'published' => null,
    'url' => null,
    'file' => null,
    'size' => 0,
    'countries' => '',
]);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $latest = ver_safe((string)($_POST['latest'] ?? ''));
    $notes = trim((string)($_POST['notes'] ?? ''));
    $force = isset($_POST['force']);
    $active = isset($_POST['active']);
    $mode = ($_POST['mode'] ?? 'upload') === 'url' ? 'url' : 'upload';
    $countries = country_field();

    if ($latest === '') {
        flash('شمارهٔ نسخه اجباری است.', 'err');
        header('Location: version.php');
        exit;
    }

    $url = $ver['url'] ?? null;
    $file = $ver['file'] ?? null;
    $sha = '';
    $size = 0;

    if ($mode === 'upload') {
        if (!isset($_FILES['zip']) || $_FILES['zip']['error'] === UPLOAD_ERR_NO_FILE) {
            flash('فایل zip آپدیت را انتخاب کنید (یا حالت لینک مستقیم را بزنید).', 'err');
            header('Location: version.php');
            exit;
        }
        $f = $_FILES['zip'];
        if ($f['error'] !== UPLOAD_ERR_OK) {
            flash('خطا در آپلود فایل (کد ' . (int)$f['error'] . '). محدودیت سرور را بررسی کنید.', 'err');
            header('Location: version.php');
            exit;
        }
        $name = strtolower((string)$f['name']);
        if (substr($name, -4) !== '.zip') {
            flash('فقط فایل zip پذیرفته می‌شود.', 'err');
            header('Location: version.php');
            exit;
        }
        $destName = 'roxa-' . $latest . '.zip';
        $dest = DL_DIR . '/' . $destName;
        if (!is_dir(DL_DIR)) {
            @mkdir(DL_DIR, 0755, true);
        }
        if (!move_uploaded_file($f['tmp_name'], $dest)) {
            flash('انتقال فایل به پوشه dl ممکن نشد — دسترسی پوشه را چک کنید.', 'err');
            header('Location: version.php');
            exit;
        }
        @chmod($dest, 0644);
        $file = $destName;
        $url = null;
        $sha = hash_file('sha256', $dest) ?: '';
        $size = (int)filesize($dest);
    } else {
        $url = trim((string)($_POST['url'] ?? ''));
        if ($url === '' || !preg_match('#^https?://#i', $url)) {
            flash('لینک مستقیم دانلود باید با http یا https شروع شود.', 'err');
            header('Location: version.php');
            exit;
        }
        $sha = strtolower(preg_replace('/[^0-9a-fA-F]/', '', (string)($_POST['sha'] ?? '')) ?? '');
        $file = null;
        $url = str_cut($url, 400);
    }

    jwrite($storeFile, [
        'latest' => $latest,
        'notes' => str_cut($notes, 4000),
        'sha256' => $sha,
        'force' => $force,
        'active' => $active,
        'published' => date('c'),
        'url' => $url,
        'file' => $file,
        'size' => $size,
        'countries' => $countries,
    ]);
    flash($active ? 'نسخه ' . $latest . ' منتشر شد.' : 'نسخه ' . $latest . ' ذخیره شد (غیرفعال — برای انتشار تیک فعال را بزنید).');
    header('Location: version.php');
    exit;
}

page_head('نسخه و آپدیت', 'ver');

$activeBadge = !empty($ver['active'])
    ? '<span class="badge b-on">در حال انتشار</span>'
    : '<span class="badge b-off">انتشار نشده</span>';

echo '<div class="grid">';
echo '<div class="stat"><div class="k">نسخهٔ فعلی سرور</div><div class="v">' . h((string)($ver['latest'] ?: '—')) . '</div></div>';
echo '<div class="stat"><div class="k">وضعیت</div><div class="v">' . $activeBadge . '</div></div>';
echo '<div class="stat"><div class="k">اجباری</div><div class="v"><span class="badge ' . (!empty($ver['force']) ? 'b-warn">بله' : 'b-off">خیر') . '</span></div></div>';
echo '<div class="stat"><div class="k">اندازهٔ فایل</div><div class="v"><small>' . (!empty($ver['size']) ? number_format((int)$ver['size'] / 1048576, 1) . ' MB' : '—') . '</small></div></div>';
echo '</div>';

if (!empty($ver['latest'])) {
    echo '<div class="card"><h2>اطلاعات انتشار فعلی</h2><div class="hint">';
    if (!empty($ver['url'])) {
        echo 'لینک دانلود: <code>' . h($ver['url']) . '</code><br>';
    } elseif (!empty($ver['file'])) {
        echo 'لینک دانلود: <code>' . h(BASE_URL . '/dl/' . rawurlencode($ver['file'])) . '</code><br>';
    }
    if (!empty($ver['sha256'])) {
        echo 'SHA-256: <span class="mono">' . h($ver['sha256']) . '</span><br>';
    }
    $ics = (string)($ver['countries'] ?? '');
    echo 'کشورهای هدف: <code>' . ($ics !== '' ? h($ics) : 'همه') . '</code>';
    echo '</div></div>';
}

echo '<div class="card"><h2>انتشار نسخه جدید</h2>';
echo '<form method="post" enctype="multipart/form-data">' . csrf_field();
echo '<div class="row"><div><label>شمارهٔ نسخه</label><input type="text" name="latest" placeholder="1.1.0" value="" required></div>';
echo '<div><label>نحوهٔ تحویل فایل</label><select name="mode" id="mode" onchange="document.getElementById(\'up\').style.display=this.value===\'upload\'?\'\':\'none\';document.getElementById(\'ur\').style.display=this.value===\'url\'?\'\':\'none\'">';
echo '<option value="upload">آپلود zip از اینجا</option><option value="url">لینک مستقیم (هاست دیگر)</option></select></div></div>';
echo '<div id="up"><label>فایل zip آپدیت (محتوای پوشهٔ برنامه)</label><input type="file" name="zip" accept=".zip">';
echo '<div class="hint">محدودیت آپلود سرور: ' . h((string)ini_get('upload_max_filesize')) . ' (POST: ' . h((string)ini_get('post_max_size')) . ') — اگر zip بزرگ‌تر است از «لینک مستقیم» استفاده کنید.</div></div>';
echo '<div id="ur" style="display:none"><label>لینک مستقیم فایل zip</label><input type="text" name="url" placeholder="https://.../roxa.zip">';
echo '<label>هش SHA-256 فایل (اختیاری، برای اعتبارسنجی کلاینت)</label><input type="text" name="sha" placeholder="64 کاراکتر hex" dir="ltr"></div>';
echo '<label>یادداشت انتشار (برای کاربر نمایش داده می‌شود)</label><textarea name="notes" placeholder="مثلاً: بهبود پایداری اتصال و رفع باگ TUN"></textarea>';
echo '<label>کشورهای هدف (اختیاری)</label><input type="text" name="countries" placeholder="IR,TR — خالی = همه کشورها">';
echo '<div class="hint">کدهای ISO دوحرفه‌ای که آپدیت فقط به کاربران آن‌ها نمایش داده شود؛ خالی = همه کشورها.</div>';
echo '<div class="check"><input type="checkbox" name="force" id="force"><label for="force" style="margin:0">آپدیت اجباری (کاربر مجبور به نصب باشد)</label></div>';
echo '<div class="check"><input type="checkbox" name="active" id="active" checked><label for="active" style="margin:0">فعال — بلافاصله برای کاربران قابل دریافت</label></div>';
echo '<button class="btn" type="submit">انتشار / ذخیره نسخه</button></form></div>';

echo '<div class="card"><h2>راهنما</h2><div class="hint">';
echo '۱. نسخهٔ جدید را با همان ساختار پوشهٔ برنامه در یک فایل zip بریزید (فایل‌های exe و dll و پوشه‌ها در ریشهٔ zip).<br>';
echo '۲. شمارهٔ نسخه را وارد کنید (مثلاً <code>1.1.0</code>)، zip را آپلود و تیک «فعال» را بزنید.<br>';
echo '۳. اپ کاربران با باز شدن برنامه این نسخه را چک می‌کند؛ اگر جدیدتر باشد بنر نمایش می‌دهد و با کلیک روی «نصب» خودکار دانلود و جایگزین می‌شود.';
echo '</div></div>';

page_foot();
