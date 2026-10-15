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
    'langs' => '*',
]);

if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    // درخواست‌های AJAX (با نوار پیشرفت) به‌جای ریدایرکت، پاسخ JSON می‌گیرند.
    $isAjax = strtolower((string)($_SERVER['HTTP_X_REQUESTED_WITH'] ?? '')) === 'xmlhttprequest';

    /**
     * پاسخ پایانی: JSON برای نوار پیشرفت، وگرنه رفتار قبلی (flash + ریدایرکت).
     * در حالت موفقیتِ AJAX هم flash می‌نویسیم تا بعد از تازه‌سازی صفحه پیام بماند.
     */
    $respond = static function (bool $ok, string $msg) use ($isAjax): void {
        if ($isAjax) {
            if ($ok) {
                flash($msg);
            }
            header('Content-Type: application/json; charset=utf-8');
            echo json_encode(['ok' => $ok, 'msg' => $msg], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            exit;
        }
        flash($msg, $ok ? 'ok' : 'err');
        header('Location: version.php');
        exit;
    };

    if ($isAjax) {
        // csrf_check در حالت AJAX با خروجی متنی 419 تمام می‌شود؛ همین‌جا با پیام فارسی پاسخ می‌دهیم.
        sess_start();
        $token = (string)($_POST['csrf'] ?? '');
        if ($token === '' || empty($_SESSION['csrf']) || !hash_equals((string)$_SESSION['csrf'], $token)) {
            $respond(false, 'نشست شما منقضی شده است؛ صفحه را تازه‌سازی کنید و دوباره تلاش کنید.');
        }
    } else {
        csrf_check();
    }

    $latest = ver_safe((string)($_POST['latest'] ?? ''));
    $notes = trim((string)($_POST['notes'] ?? ''));
    $force = isset($_POST['force']);
    $active = isset($_POST['active']);
    $mode = ($_POST['mode'] ?? 'upload') === 'url' ? 'url' : 'upload';
    $countries = country_field();
    $langs = langs_field();

    if ($latest === '') {
        $respond(false, 'شمارهٔ نسخه اجباری است.');
    }

    $url = $ver['url'] ?? null;
    $file = $ver['file'] ?? null;
    $sha = '';
    $size = 0;

    if ($mode === 'upload') {
        if (!isset($_FILES['zip']) || $_FILES['zip']['error'] === UPLOAD_ERR_NO_FILE) {
            $respond(false, 'فایل zip آپدیت را انتخاب کنید (یا حالت لینک مستقیم را بزنید).');
        }
        $f = $_FILES['zip'];
        if ($f['error'] !== UPLOAD_ERR_OK) {
            $respond(false, 'خطا در آپلود فایل (کد ' . (int)$f['error'] . '). محدودیت سرور را بررسی کنید.');
        }
        $name = strtolower((string)$f['name']);
        if (substr($name, -4) !== '.zip') {
            $respond(false, 'فقط فایل zip پذیرفته می‌شود.');
        }
        $destName = 'roxa-' . $latest . '.zip';
        $dest = DL_DIR . '/' . $destName;
        if (!is_dir(DL_DIR)) {
            @mkdir(DL_DIR, 0755, true);
        }
        if (!move_uploaded_file($f['tmp_name'], $dest)) {
            $respond(false, 'انتقال فایل به پوشه dl ممکن نشد — دسترسی پوشه را چک کنید.');
        }
        @chmod($dest, 0644);
        $file = $destName;
        $url = null;
        $sha = hash_file('sha256', $dest) ?: '';
        $size = (int)filesize($dest);
    } else {
        // کلاینت عمداً فقط https را می‌پذیرد؛ لینکِ http در پنل پذیرفته نمی‌شود
        // تا نسخهٔ منتشرشده از همان اول قابلِ دانلود باشد.
        $url = safe_url(trim((string)($_POST['url'] ?? '')));
        if ($url === null || $url === '') {
            $respond(false, 'لینک مستقیم دانلود باید با https:// شروع شود.');
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
        'langs' => $langs,
    ]);
    $respond(true, $active ? 'نسخه ' . $latest . ' منتشر شد.' : 'نسخه ' . $latest . ' ذخیره شد (غیرفعال — برای انتشار تیک فعال را بزنید).');
}

page_head('نسخه و آپدیت', 'ver');

$activeBadge = !empty($ver['active'])
    ? '<span class="badge b-on">در حال انتشار</span>'
    : '<span class="badge b-off">انتشار نشده</span>';

// سقف واقعی آپلود = کوچک‌ترین مقدار مثبتِ upload_max_filesize و post_max_size.
// هم در راهنما نمایش داده می‌شود و هم پیش از ارسال فرم در مرورگر چک می‌شود تا
// به‌جای صفحهٔ خطای 413، یک پیام فارسی واضح ببینید و فایل بالاتر از سقف انتخاب نشود.
$capUp = ini_bytes((string)ini_get('upload_max_filesize'));
$capPost = ini_bytes((string)ini_get('post_max_size'));
$caps = [];
if ($capUp > 0) {
    $caps[] = $capUp;
}
if ($capPost > 0) {
    $caps[] = $capPost;
}
$cap = $caps ? min($caps) : 0;
$capMb = $cap > 0 ? number_format($cap / 1048576, 0, '.', '') : '0';

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
    $ivl = langs_parse((string)($ver['langs'] ?? ''));
    echo 'زبان‌های هدف: <code>' . ($ivl ? h(implode(',', $ivl)) : 'همه') . '</code><br>';
    echo 'کشورهای هدف: <code>' . ($ics !== '' ? h($ics) : 'همه') . '</code>';
    echo '</div></div>';
}

echo '<div class="card"><h2>انتشار نسخه جدید</h2>';
echo '<form method="post" enctype="multipart/form-data" id="verform">' . csrf_field();
echo '<div class="row"><div><label>شمارهٔ نسخه</label><input type="text" name="latest" placeholder="1.1.0" value="" required></div>';
echo '<div><label>نحوهٔ تحویل فایل</label><select name="mode" id="mode" onchange="document.getElementById(\'up\').style.display=this.value===\'upload\'?\'\':\'none\';document.getElementById(\'ur\').style.display=this.value===\'url\'?\'\':\'none\'">';
echo '<option value="upload">آپلود zip از اینجا</option><option value="url">لینک مستقیم (هاست دیگر)</option></select></div></div>';
echo '<div id="up"><label>فایل zip آپدیت (محتوای پوشهٔ برنامه)</label><input type="file" name="zip" id="zipfile" accept=".zip">';
echo '<div class="hint">محدودیت آپلود سرور: ' . h((string)ini_get('upload_max_filesize')) . ' (POST: ' . h((string)ini_get('post_max_size')) . ') — سقف مؤثر <b>' . h($capMb) . ' مگابایت</b> است؛ فایل بزرگ‌تر از این، ارسال نمی‌شود و باید از «لینک مستقیم» استفاده کنید.</div></div>';
echo '<div id="ur" style="display:none"><label>لینک مستقیم فایل zip</label><input type="text" name="url" placeholder="https://.../roxa.zip">';
echo '<label>هش SHA-256 فایل (اختیاری، برای اعتبارسنجی کلاینت)</label><input type="text" name="sha" placeholder="64 کاراکتر hex" dir="ltr"></div>';
echo '<label>یادداشت انتشار (برای کاربر نمایش داده می‌شود)</label><textarea name="notes" placeholder="مثلاً: بهبود پایداری اتصال و رفع باگ TUN"></textarea>';
echo '<label>کشورهای هدف (اختیاری)</label><input type="text" name="countries" placeholder="IR,TR — خالی = همه کشورها">';
echo '<div class="hint">کدهای ISO دوحرفه‌ای که آپدیت فقط به کاربران آن‌ها نمایش داده شود؛ خالی = همه کشورها.</div>';
echo lang_picker();
echo '<div class="check"><input type="checkbox" name="force" id="force"><label for="force" style="margin:0">آپدیت اجباری (کاربر مجبور به نصب باشد)</label></div>';
echo '<div class="check"><input type="checkbox" name="active" id="active" checked><label for="active" style="margin:0">فعال — بلافاصله برای کاربران قابل دریافت</label></div>';
echo '<div class="up-progress" id="upprog">';
echo '<div class="up-track"><div class="up-fill" id="upfill"></div></div>';
echo '<div class="up-meta">';
echo '<span>درصد: <b id="uppct">۰٪</b></span>';
echo '<span id="upsize">—</span>';
echo '<span id="upspeed">—</span>';
echo '<span>زمان باقی‌مانده: <b id="upeta">—</b></span>';
echo '</div></div>';
echo '<div class="up-msg" id="upmsg"></div>';
echo '<button class="btn" type="submit" id="pubbtn">انتشار / ذخیره نسخه</button></form></div>';

echo '<div class="card"><h2>راهنما</h2><div class="hint">';
echo '۱. نسخهٔ جدید را با همان ساختار پوشهٔ برنامه در یک فایل zip بریزید (فایل‌های exe و dll و پوشه‌ها در ریشهٔ zip).<br>';
echo '۲. شمارهٔ نسخه را وارد کنید (مثلاً <code>1.1.0</code>)، zip را آپلود و تیک «فعال» را بزنید.<br>';
echo '۳. اپ کاربران با باز شدن برنامه این نسخه را چک می‌کند؛ اگر جدیدتر باشد بنر نمایش می‌دهد و با کلیک روی «نصب» خودکار دانلود و جایگزین می‌شود.';
echo '</div></div>';

// نوار پیشرفت بارگذاری + چک سقف + ارسال AJAX تا پاسخ سرور (JSON) همان‌جا نمایش داده شود.
echo '<style>
.up-progress{display:none;margin-top:16px}
.up-track{height:10px;background:#0d1322;border:1px solid #1f2937;border-radius:999px;overflow:hidden}
.up-fill{height:100%;width:0;background:linear-gradient(90deg,#7c3aed,#4f46e5);transition:width .2s linear}
.up-meta{display:flex;justify-content:space-between;gap:8px 16px;flex-wrap:wrap;font-size:12px;color:#9ca3af;margin-top:8px}
.up-meta b{color:#e5e7eb;font-weight:700;direction:ltr;display:inline-block}
.up-msg{display:none;padding:11px 15px;border-radius:10px;margin-top:14px;font-size:13px;line-height:1.9}
.up-msg.ok{background:#062a1c;color:#34d399;border:1px solid #0d4a33}
.up-msg.err{background:#2a0e0e;color:#f87171;border:1px solid #4a0d0d}
#pubbtn[disabled]{opacity:.6;cursor:not-allowed}
</style>';

echo '<script>
(function () {
  var cap = ' . (int)$cap . ';
  var capMb = ' . json_encode($capMb) . ';
  var form = document.getElementById("verform");
  var zip = document.getElementById("zipfile");
  var mode = document.getElementById("mode");
  var btn = document.getElementById("pubbtn");
  var prog = document.getElementById("upprog");
  var fill = document.getElementById("upfill");
  var pct = document.getElementById("uppct");
  var size = document.getElementById("upsize");
  var speed = document.getElementById("upspeed");
  var eta = document.getElementById("upeta");
  var msg = document.getElementById("upmsg");
  var busy = false;

  function toMb(n) { return (n / 1048576).toFixed(1) + " MB"; }

  function left(sec) {
    if (!isFinite(sec) || sec < 0) return "—";
    sec = Math.ceil(sec);
    if (sec < 60) return sec + " ثانیه";
    var m = Math.floor(sec / 60);
    var s = sec % 60;
    return m + ":" + (s < 10 ? "0" : "") + s + " دقیقه";
  }

  function say(text, kind) {
    msg.className = "up-msg " + kind;
    msg.textContent = text;
    msg.style.display = "block";
  }

  function idle() {
    busy = false;
    btn.disabled = false;
    btn.textContent = "انتشار / ذخیره نسخه";
  }

  if (!form || !btn) return;

  form.addEventListener("submit", function (e) {
    e.preventDefault();
    if (busy) return;
    msg.style.display = "none";

    // حالت «لینک مستقیم»: ارسال معمولی فرم، چون فایلی آپلود نمی‌شود.
    if (mode && mode.value !== "upload") {
      form.submit();
      return;
    }

    if (!zip || !zip.files || !zip.files.length) {
      say("برای انتشار، اول فایل zip آپدیت را انتخاب کنید.", "err");
      return;
    }

    var file = zip.files[0];
    if (cap > 0 && file.size > cap) {
      say("حجم فایل انتخاب‌شده " + toMb(file.size) + " است و از سقف مؤثر آپلود سرور (" + capMb + " MB) بیشتر است. "
        + "upload_max_filesize و post_max_size را در پنل هاست یا php.ini بالا ببرید، یا در «نحوهٔ تحویل فایل» گزینهٔ «لینک مستقیم» را انتخاب کنید.", "err");
      return;
    }

    var data = new FormData(form);
    var started = Date.now();
    var total = file.size;
    var xhr = new XMLHttpRequest();

    busy = true;
    btn.disabled = true;
    btn.textContent = "در حال بارگذاری…";
    prog.style.display = "block";
    fill.style.width = "0%";
    pct.textContent = "۰٪";
    size.textContent = "0 / " + toMb(total);
    speed.textContent = "—";
    eta.textContent = "محاسبه…";

    xhr.upload.addEventListener("progress", function (ev) {
      if (!ev.lengthComputable) return;
      total = ev.total || total;
      var done = ev.loaded;
      var p = done / total;
      fill.style.width = (p * 100).toFixed(1) + "%";
      pct.textContent = Math.floor(p * 100) + "٪";
      size.textContent = toMb(done) + " / " + toMb(total);
      var sec = (Date.now() - started) / 1000;
      var rate = sec > 0 ? done / sec : 0;
      speed.textContent = rate > 0 ? (rate / 1048576).toFixed(1) + " MB/s" : "—";
      if (p >= 1) {
        eta.textContent = "در حال پردازش سمت سرور…";
      } else {
        eta.textContent = left((total - done) / (rate > 0 ? rate : 1));
      }
    });

    xhr.addEventListener("load", function () {
      var res = null;
      try { res = JSON.parse(xhr.responseText); } catch (err) {}
      if (xhr.status >= 200 && xhr.status < 300 && res && res.ok) {
        fill.style.width = "100%";
        pct.textContent = "۱۰۰٪";
        speed.textContent = "—";
        eta.textContent = "تمام شد";
        idle();
        say(res.msg, "ok");
        setTimeout(function () { window.location.reload(); }, 2500);
        return;
      }
      idle();
      if (res && res.msg) {
        say(res.msg, "err");
      } else if (xhr.status === 413) {
        say("حجم درخواست از سقف سرور بیشتر است؛ upload_max_filesize و post_max_size را بالا ببرید یا از «لینک مستقیم» استفاده کنید.", "err");
      } else if (xhr.status === 419) {
        say("نشست شما منقضی شده است؛ صفحه را تازه‌سازی کنید و دوباره تلاش کنید.", "err");
      } else if (!res && xhr.status === 200) {
        say("پاسخ سرور قابل فهم نبود؛ احتمالاً حجم درخواست از سقف post_max_size بیشتر است. سقف آپلود را در پنل هاست بالا ببرید یا از «لینک مستقیم» استفاده کنید.", "err");
      } else {
        say("خطا در ارسال فایل (کد " + xhr.status + "). صفحه را تازه‌سازی کنید و دوباره تلاش کنید.", "err");
      }
    });

    xhr.addEventListener("error", function () {
      idle();
      say("اتصال به سرور قطع شد و فایل ارسال نشد. اتصال اینترنت را بررسی کنید و دوباره تلاش کنید.", "err");
    });

    xhr.addEventListener("abort", function () {
      idle();
      say("ارسال فایل لغو شد.", "err");
    });

    xhr.open("POST", form.action || window.location.href, true);
    xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");
    xhr.send(data);
  });
})();
</script>';

page_foot();
