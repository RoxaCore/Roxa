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
    // AJAX requests (with progress bar) receive a JSON response instead of a redirect.
    $isAjax = strtolower((string)($_SERVER['HTTP_X_REQUESTED_WITH'] ?? '')) === 'xmlhttprequest';

    /**
     * Final response: JSON for the progress bar, otherwise the old behavior
     * (flash + redirect). On AJAX success we also flash so the message survives a refresh.
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
        // csrf_check in AJAX mode ends with a plain 419 output; here we reply with a Persian message instead.
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
        // The client deliberately accepts only https; an http link is not accepted
        // in the panel so the published version is downloadable from the start.
        $url = safe_url(trim((string)($_POST['url'] ?? '')));
        if ($url === null || $url === '') {
            $respond(false, 'لینک مستقیم دانلود باید با https:// شروع شود.');
        }
        $sha = strtolower(preg_replace('/[^0-9a-fA-F]/', '', (string)($_POST['sha'] ?? '')) ?? '');
        $file = null;
        $url = str_cut($url, 400);
    }

    version_publish($latest, $notes, $force, $active, $url, $file, $sha, $size, $countries, $langs);
    $respond(true, $active ? 'نسخه ' . $latest . ' منتشر شد.' : 'نسخه ' . $latest . ' ذخیره شد (غیرفعال — برای انتشار تیک فعال را بزنید).');
}

page_head('نسخه و آپدیت', 'ver');

$activeBadge = !empty($ver['active'])
    ? '<span class="badge b-on">در حال انتشار</span>'
    : '<span class="badge b-off">انتشار نشده</span>';

// No per-request cap check here anymore: the browser now uploads in 512KB
// chunks (admin/upart.php), so upload_max_filesize / post_max_size no longer
// gate publishing. The values are only shown in the form hint for reference.

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
echo '<div class="hint">آپلود به‌صورت قطعه‌های ۵۱۲KB انجام می‌شود و سقفِ آپلودِ PHP دیگر مانعِ فایل‌های بزرگ نیست (حداکثر حجم کل: ' . (int)round(UPART_TOTAL_MAX / 1048576) . ' مگابایت). سقف فعلیِ سرور: ' . h((string)ini_get('upload_max_filesize')) . ' (POST: ' . h((string)ini_get('post_max_size')) . ').</div></div>';
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

// Upload progress bar + cap check + AJAX submit so the server's (JSON) response is shown right there.
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
  var maxTotal = ' . (int)UPART_TOTAL_MAX . ';
  var CHUNK = 524288; // 512KB — under typical per-request proxy limits (nginx default 1MB)
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
  var csrfInput = form ? form.querySelector("input[name=csrf]") : null;
  var csrfVal = csrfInput ? csrfInput.value : "";
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

  function token() {
    var a = new Uint8Array(16);
    (window.crypto || window.msCrypto).getRandomValues(a);
    var s = "";
    for (var i = 0; i < a.length; i++) s += ("0" + a[i].toString(16)).slice(-2);
    return s;
  }

  function wait(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }

  function post(fd, onProgress) {
    return new Promise(function (resolve, reject) {
      var xhr = new XMLHttpRequest();
      xhr.open("POST", "upart.php", true);
      xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");
      if (onProgress) {
        xhr.upload.addEventListener("progress", function (ev) {
          if (ev.lengthComputable) onProgress(ev.loaded);
        });
      }
      xhr.addEventListener("load", function () {
        var res = null;
        try { res = JSON.parse(xhr.responseText); } catch (err) {}
        if (xhr.status >= 200 && xhr.status < 300 && res && res.ok) { resolve(res); return; }
        var er = new Error((res && res.msg) ? res.msg : "");
        er.fromServer = !!(res && res.msg);
        er.status = xhr.status;
        reject(er);
      });
      xhr.addEventListener("error", function () {
        var er = new Error("");
        er.fromServer = false;
        reject(er);
      });
      xhr.addEventListener("abort", function () {
        var er = new Error("");
        er.fromServer = false;
        er.aborted = true;
        reject(er);
      });
      xhr.send(fd);
    });
  }

  if (!form || !btn) return;

  form.addEventListener("submit", function (e) {
    e.preventDefault();
    if (busy) return;
    msg.style.display = "none";

    // «لینک مستقیم» mode: normal form submission, since no file is uploaded.
    if (mode && mode.value !== "upload") {
      form.submit();
      return;
    }

    if (!zip || !zip.files || !zip.files.length) {
      say("برای انتشار، اول فایل zip آپدیت را انتخاب کنید.", "err");
      return;
    }

    var file = zip.files[0];
    if (file.size > maxTotal) {
      say("حجم فایل انتخاب‌شده " + toMb(file.size) + " است و از حداکثر مجاز (" + Math.round(maxTotal / 1048576) + " MB) بیشتر است.", "err");
      return;
    }

    var started = Date.now();
    var totalSize = file.size;
    var parts = Math.max(1, Math.ceil(totalSize / CHUNK));
    var tk = token();
    var sentBytes = 0;

    busy = true;
    btn.disabled = true;
    btn.textContent = "در حال بارگذاری…";
    prog.style.display = "block";
    fill.style.width = "0%";
    pct.textContent = "۰٪";
    size.textContent = "0 / " + toMb(totalSize);
    speed.textContent = "—";
    eta.textContent = "محاسبه…";

    function render(done) {
      var p = totalSize > 0 ? done / totalSize : 1;
      fill.style.width = (p * 100).toFixed(1) + "%";
      pct.textContent = Math.floor(p * 100) + "٪";
      size.textContent = toMb(done) + " / " + toMb(totalSize);
      var sec = (Date.now() - started) / 1000;
      var rate = sec > 0 ? done / sec : 0;
      speed.textContent = rate > 0 ? (rate / 1048576).toFixed(1) + " MB/s" : "—";
      if (p >= 1) {
        eta.textContent = "در حال پردازش سمت سرور…";
      } else {
        eta.textContent = left((totalSize - done) / (rate > 0 ? rate : 1));
      }
    }

    function sendChunk(i, attempt) {
      var start = i * CHUNK;
      var blob = file.slice(start, Math.min(totalSize, start + CHUNK));
      var fd = new FormData();
      fd.append("act", "chunk");
      fd.append("csrf", csrfVal);
      fd.append("token", tk);
      fd.append("index", String(i));
      fd.append("total", String(parts));
      fd.append("part", blob, "part.bin");
      var base = sentBytes;
      return post(fd, function (loaded) {
        render(base + loaded);
      }).then(function () {
        sentBytes = base + blob.size;
        render(sentBytes);
      }).catch(function (er) {
        if (er.aborted) throw er;
        // A network-level hiccup on one 512KB piece is retried in place;
        // a real server answer (CSRF, validation) is shown right away.
        if (!er.fromServer && attempt < 3) {
          return wait(700 * attempt).then(function () {
            return sendChunk(i, attempt + 1);
          });
        }
        throw er;
      });
    }

    var chain = Promise.resolve();
    for (var i = 0; i < parts; i++) {
      chain = chain.then(function (idx) {
        return function () { return sendChunk(idx, 1); };
      }(i));
    }

    chain.then(function () {
      var fd = new FormData(form);
      fd.delete("zip");
      fd.append("act", "finish");
      fd.append("token", tk);
      fd.append("total", String(parts));
      return post(fd);
    }).then(function (res) {
      fill.style.width = "100%";
      pct.textContent = "۱۰۰٪";
      speed.textContent = "—";
      eta.textContent = "تمام شد";
      idle();
      say(res.msg, "ok");
      setTimeout(function () { window.location.reload(); }, 2500);
    }).catch(function (er) {
      idle();
      if (er && er.aborted) {
        say("ارسال فایل لغو شد.", "err");
        return;
      }
      if (er && er.fromServer) {
        if (er.status === 419) {
          say("نشست شما منقضی شده است؛ صفحه را تازه‌سازی کنید و دوباره تلاش کنید.", "err");
        } else {
          say(er.message, "err");
        }
        return;
      }
      say("آپلود یکی از قطعه‌ها ناموفق ماند و اتصال قطع شد. بعد از چند تلاش خودکار، دکمهٔ انتشار را دوباره بزنید؛ اگر ادامه داد از «لینک مستقیم» استفاده کنید.", "err");
    });
  });
})();
</script>';

page_foot();
