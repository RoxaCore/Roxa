<?php
require_once __DIR__ . '/config.php';

date_default_timezone_set(TZ);

function ini_bytes(string $val): int
{
    $val = strtolower(trim($val));
    if ($val === '' || $val === '-1' || $val === 'off' || $val === '0') {
        return 0;
    }
    $n = (float)$val;
    if (substr($val, -1) === 'g') {
        $n *= 1073741824;
    } elseif (substr($val, -1) === 'm') {
        $n *= 1048576;
    } elseif (substr($val, -1) === 'k') {
        $n *= 1024;
    }
    return (int)$n;
}

function post_size_exceeded(): bool
{
    $max = ini_bytes((string)ini_get('post_max_size'));
    if ($max <= 0) {
        return false;
    }
    $len = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    return $len > $max;
}

// چک زودهنگام: اگر POST بزرگ‌تر از post_max_size باشد، PHP $_POST را خالی می‌کند و
// csrf_check به‌جای خطای درست، 419/CSRF یا زنجیرهٔ warning می‌دهد. زودتر از session خارج می‌شویم.
if (post_size_exceeded()) {
    // اگر PHP قبلاً Warning چاپ کرده باشد، headers_sent() درست است و دیگر نباید
    // http_response_code/header صدا زده شود (وگرنه دو Warning اضافه تولید می‌شود).
    if (!headers_sent()) {
        http_response_code(413);
        header('Content-Type: text/plain; charset=utf-8');
    }
    $limit = (string)ini_get('post_max_size');
    $got = (int)($_SERVER['CONTENT_LENGTH'] ?? 0);
    $gotMb = $got > 0 ? number_format($got / 1048576, 1) . ' MB' : 'نامشخص';
    exit(
        'حجم درخواست POST (' . $gotMb . ') از محدودیت سرور (post_max_size = ' . $limit . ') بیشتر است.' . "\n" .
        'برای انتشار نسخه یکی از کارهای زیر را انجام دهید:' . "\n" .
        '  ۱) upload_max_filesize و post_max_size را در php.ini / .user.ini / پنل هاست بالا ببرید،' . "\n" .
        '  ۲) یا در پنل «نسخه و آپدیت» به‌جای آپلود، از حالت «لینک مستقیم» استفاده کنید.' . "\n"
    );
}

function sess_start(): void
{
    if (session_status() === PHP_SESSION_ACTIVE) {
        return;
    }
    $secure = !empty($_SERVER['HTTPS']) && $_SERVER['HTTPS'] !== 'off';
    session_name('ROXAPANEL');
    session_set_cookie_params([
        'lifetime' => 0,
        'path' => '/',
        'secure' => $secure,
        'httponly' => true,
        'samesite' => 'Lax',
    ]);
    session_start();
    if (isset($_SESSION['auth_at']) && time() - $_SESSION['auth_at'] > SESSION_LIFETIME) {
        $_SESSION = [];
        session_destroy();
        session_start();
    }
}

/**
 * خواندنِ زیرِ قفلِ اشتراکی. بدونِ این، خوانندهٔ همزمان می‌تواند بینِ truncate
 * و write فایلِ ناقص ببیند.
 */
function jread(string $file, array $default): array
{
    if (!is_file($file)) {
        return $default;
    }
    $fp = @fopen($file, 'rb');
    if (!$fp) {
        return $default;
    }
    try {
        flock($fp, LOCK_SH);
        $raw = stream_get_contents($fp);
    } finally {
        flock($fp, LOCK_UN);
        fclose($fp);
    }
    if ($raw === false || $raw === '') {
        return $default;
    }
    $data = json_decode($raw, true);
    return is_array($data) ? $data : $default;
}

/**
 * نوشتنِ اتمیک: فایلِ موقتِ همنام + rename. خوانندهٔ همزمان یا نسخهٔ قبلی را
 * می‌بیند یا بعدی را — هیچ‌وقت ناقص را.
 */
function jwrite(string $file, array $data): void
{
    $dir = dirname($file);
    if (!is_dir($dir)) {
        @mkdir($dir, 0700, true);
    }
    $json = json_encode($data, JSON_UNESCAPED_UNICODE | JSON_PRETTY_PRINT | JSON_UNESCAPED_SLASHES);
    if ($json === false) {
        throw new RuntimeException('encode failed');
    }
    $tmp = $dir . '/.' . basename($file) . '.' . bin2hex(random_bytes(6)) . '.tmp';
    if (file_put_contents($tmp, $json, LOCK_EX) === false) {
        throw new RuntimeException('write failed');
    }
    @chmod($tmp, 0600);
    if (!rename($tmp, $file)) {
        @unlink($tmp);
        throw new RuntimeException('rename failed');
    }
}

/**
 * خواندن، تغییر و نوشتنِ زیرِ یک قفلِ واحد.
 *
 * کالبک آرایه را می‌گیرد و **باید** صریح یکی از این‌ها را برگرداند:
 *   - آرایهٔ تغییریافته: نوشته می‌شود
 *   - false: هیچ‌چیز نوشته نشود (مثلاً سقف پر است یا شرطی برقرار نیست)
 *
 * چون آرایه در PHP مقداری است، تغییرِ محلیِ پارامتر به بیرون منتقل نمی‌شود.
 * برگرداندنِ null (یعنی کالبکِ بدونِ return) خطا می‌دهد، چون رایج‌ترین راهِ
 * خراب‌کردنِ ساکتِ این تابع همین است: فراموش کردنِ return و نوشته‌شدنِ همان
 * ورودیِ بی‌تغییر.
 *
 * قفل رویِ فایلِ جداگانهٔ `.lock` گرفته می‌شود تا read-modify-writeِ چند
 * درخواستِ همزمان از دست نرود و آیتم گم یا تکراری نشود.
 */
function jupdate(string $file, callable $mutator)
{
    $dir = dirname($file);
    if (!is_dir($dir)) {
        @mkdir($dir, 0700, true);
    }
    $lock = @fopen($file . '.lock', 'c');
    if (!$lock) {
        throw new RuntimeException('lock failed');
    }
    flock($lock, LOCK_EX);
    try {
        $data = jread($file, []);
        $out = $mutator($data);
        if ($out === false) {
            return false;
        }
        if (!is_array($out)) {
            throw new InvalidArgumentException('jupdate mutator must return an array or false');
        }
        $data = $out;
        jwrite($file, $data);
        return true;
    } finally {
        flock($lock, LOCK_UN);
        fclose($lock);
    }
}

function h(?string $s): string
{
    return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8');
}

function csrf_token(): string
{
    sess_start();
    if (empty($_SESSION['csrf'])) {
        $_SESSION['csrf'] = bin2hex(random_bytes(32));
    }
    return $_SESSION['csrf'];
}

function csrf_field(): string
{
    return '<input type="hidden" name="csrf" value="' . h(csrf_token()) . '">';
}

function csrf_check(): void
{
    sess_start();
    $t = $_POST['csrf'] ?? '';
    if (!is_string($t) || empty($_SESSION['csrf']) || !hash_equals($_SESSION['csrf'], $t)) {
        http_response_code(419);
        exit('CSRF');
    }
}

function authed(): bool
{
    sess_start();
    return !empty($_SESSION['auth'])
        && !empty($_SESSION['auth_at'])
        && time() - $_SESSION['auth_at'] <= SESSION_LIFETIME;
}

function require_auth(): void
{
    if (!authed()) {
        header('Location: login.php');
        exit;
    }
}

function flash(string $msg, string $type = 'ok'): void
{
    sess_start();
    $_SESSION['flash'] = ['m' => $msg, 't' => $type];
}

function flash_pop(): ?array
{
    sess_start();
    if (empty($_SESSION['flash'])) {
        return null;
    }
    $f = $_SESSION['flash'];
    unset($_SESSION['flash']);
    return $f;
}

function auth_file(): string
{
    return DATA_DIR . '/auth.json';
}

function attempts_file(): string
{
    return DATA_DIR . '/attempts.json';
}

function attempts_ip_file(): string
{
    return DATA_DIR . '/attempts_ip.json';
}

/**
 * IP فقط از REMOTE_ADDR. هیچ هدرِ قابلِ جعلی (X-Forwarded-For، X-Real-IP،
 * HTTP_CLIENT_IP، Forwarded) خوانده نمی‌شود؛ پشتِ CDN هم همان IPِ لبه ثبت
 * می‌شود که برایِ نرخ‌محدودیت کافی است.
 */
function client_ip(): string
{
    $ip = (string)($_SERVER['REMOTE_ADDR'] ?? '');
    return filter_var($ip, FILTER_VALIDATE_IP) !== false ? $ip : '';
}

/**
 * نمکِ ثابت برای هش کردن IP. در فایلِ خارج از git (DATA_DIR/.salt) نگه
 * داشته می‌شود؛ بدونِ آن هشِ IP قابلِ برگشتِ آفلاین است و حفاظتِ حریم
 * خصوصی وجود ندارد.
 */
function rate_salt(): string
{
    static $salt = null;
    if ($salt !== null) {
        return $salt;
    }
    $f = DATA_DIR . '/.salt';
    if (is_file($f)) {
        $raw = @file_get_contents($f);
        if (is_string($raw) && strlen($raw) >= 32) {
            $salt = $raw;
            return $salt;
        }
    }
    $salt = bin2hex(random_bytes(32));
    if (!is_dir(DATA_DIR)) {
        @mkdir(DATA_DIR, 0700, true);
    }
    @file_put_contents($f, $salt, LOCK_EX);
    @chmod($f, 0600);
    return $salt;
}

/**
 * کلیدِ شمارندهها: نمی‌گذاریم IPِ خام در هیچ فایلِ داده ذخیره شود. کلیدِ
 * ۳۲ کاراکتریِ HMAC-SHA256 برای نرخ‌محدودیت کافی است و قابلِ برگشت نیست.
 */
function ip_key(string $ip): string
{
    // حتی IPِ خالی هم هش می‌شود تا همهٔ درخواست‌هایِ بدونِ آدرس در یک سطلِ واحد
    // جمع نشوند و بی‌دلیل همدیگر را از سقفِ نرخ خارج نکنند.
    return substr(hash_hmac('sha256', $ip, rate_salt()), 0, 32);
}

/**
 * سقفِ نگهداریِ هشِ مبدأ — نکتهٔ `src` در reports.json و `ip`/`ip_last` در stats.json.
 *
 * این هش‌ها قابلِ برگشت نیستند، ولی همچنان می‌گویند «این درخواست از همان منبعِ
 * آن یکی بوده» و برایِ مدتِ نامحدود نگه‌داشته‌نشدنی‌اند. ۹۰ روز یعنی یک فصلِ
 * کاملِ همبستگی برایِ ادمین؛ بعد از آن فقط برچسبِ منبع پاک می‌شود و خودِ رکورد
 * (گزارش یا کلاینت) سرِ جایش می‌ماند، چون تاریخچهٔ اتصال برایِ عیب‌یابی ارزش
 * دارد و آدرسِ در آن نیست.
 */
const SRC_HASH_TTL_DAYS = 90;

/**
 * آیا تاریخِ ISO 8601 قدیمی‌تر از سقف است؟
 *
 * تاریخِ خالی یا ناخوانا هم «قدیمی» حساب می‌شود: نوشتهٔ کهنه‌ای که تاریخش را
 * از دست داده، دیگر نمی‌تواند برچسبِ منبع داشته باشد، و ساکت ماندن در این
 * حالت یعنی داده برایِ همیشه می‌ماند.
 */
function src_hash_expired(string $iso): bool
{
    if ($iso === '') {
        return true;
    }
    $t = strtotime($iso);
    if ($t === false) {
        return true;
    }
    return (time() - $t) > (SRC_HASH_TTL_DAYS * 86400);
}

/**
 * پاک‌سازیِ برچسبِ منبع در reports.json. فقط `src` می‌رود؛ متنِ گزارش، خطا و لاگ
 * دست‌نخورده می‌مانند.
 *
 * false برمی‌گرداند وقتی چیزی تغییر نکرده تا jupdate بی‌دلیل رویِ دیسک ننویسد.
 */
function reports_prune_src(array $store): array
{
    if (!is_array($store['items'] ?? null)) {
        return $store;
    }
    foreach ($store['items'] as &$it) {
        if (!is_array($it)) {
            continue;
        }
        if (($it['src'] ?? '') !== '' && src_hash_expired((string)($it['at'] ?? ''))) {
            $it['src'] = '';
        }
    }
    unset($it);
    return $store;
}

/**
 * قفلِ ورود: فقط per-IP (۲۰ تلاش ناموفق/ساعت از هر آدرس).
 *
 * سقفِ سراسری عمداً قفلِ کامل نمی‌شود: اگر بشود، مهاجم با چند ده تلاشِ
 * اشتباهِ ارزان می‌تواند خودِ مدیر را برایِ یک ساعت بیرون کند (login-DoS).
 * به‌جای آن attempt_pressure() فشارِ سراسری را به تأخیرِ کوتاه یا پاسخِ ۴۲۹
 * تبدیل می‌کند.
 */
function attempt_allowed(): bool
{
    $now = time();
    $k = ip_key(client_ip());

    $ipa = jread(attempts_ip_file(), ['k' => []]);
    $e = is_array($ipa['k'][$k] ?? null) ? $ipa['k'][$k] : ['n' => 0, 'ts' => $now];
    return !((int)$e['n'] >= 20 && $now - (int)$e['ts'] < 3600);
}

/**
 * سقفِ تأخیرِ مصنوعیِ ورود، بر حسبِ ثانیه.
 *
 * چرا این‌قدر کوتاه: `sleep` در PHP یک درخواست را برایِ همان مدت به یک worker
 * گره می‌زند. اگر فشارِ سراسری بیست ثانیه خواب بدهد، مهاجم با چند صد درخواستِ
 * موازی می‌تواند کلِ pool را اشغال کند و خودِ پنل را از کار بیندازد — دقیقاً
 * همان چیزی که قفلِ سراسری می‌خواست از آن جلوگیری کند. تأخیر باید حدسِ آنلاین
 * را کُند کند، نه اینکه خودش سلاحِ حمله شود. بالایِ سقف، به‌جایِ انتظار،
 * پاسخِ ۴۲۹ِ فوری داده می‌شود.
 */
const LOGIN_DELAY_MAX = 2;

/**
 * تعدادِ تلاشِ ناموفقِ سراسری که از آن به بعد دیگر انتظار نمی‌کشیم و ۴۲۹
 * می‌دهیم. پایین‌تر از این، فقط تأخیرِ کوتاه.
 */
const LOGIN_GLOBAL_REJECT = 400;

/**
 * فشارِ سراسریِ یک ساعتِ اخیر را برمی‌گرداند:
 *   [0] تأخیرِ اضافهٔ ثانیه (۰ یا LOGIN_DELAY_MAX - ۱؛ همراهِ ثانیهٔ پایه یعنی
 *       مجموعاً ۱ تا LOGIN_DELAY_MAX ثانیه)
 *   [1] آیا اصلاً رد شود (۴۲۹) به‌جایِ انتظار
 *
 * سقفِ سراسری عمداً قفلِ کامل نمی‌شود: اگر بشود، مهاجم با چند ده تلاشِ
 * اشتباهِ ارزان می‌تواند خودِ مدیر را برایِ یک ساعت بیرون کند (login-DoS).
 */
function attempt_pressure(): array
{
    $a = jread(attempts_file(), ['n' => 0, 'ts' => 0]);
    if (time() - (int)$a['ts'] >= 3600) {
        return [0, false];
    }
    $n = (int)$a['n'];
    if ($n >= LOGIN_GLOBAL_REJECT) {
        return [0, true];
    }
    if ($n >= 100) {
        return [max(0, LOGIN_DELAY_MAX - 1), false];
    }
    return [0, false];
}

function attempt_fail(): void
{
    $now = time();
    $k = ip_key(client_ip());

    jupdate(attempts_ip_file(), function (array $st) use ($now, $k) {
        if (!is_array($st['k'] ?? null)) {
            $st['k'] = [];
        }
        // نگهداریِ کوتاه: فقط پنجرهٔ یک ساعت نگه داشته می‌شود.
        foreach ($st['k'] as $kk => $vv) {
            if (!is_array($vv) || $now - (int)($vv['ts'] ?? 0) >= 3600) {
                unset($st['k'][$kk]);
            }
        }
        $e = $st['k'][$k] ?? ['n' => 0, 'ts' => $now];
        if ($now - (int)$e['ts'] >= 3600) {
            $e = ['n' => 0, 'ts' => $now];
        }
        $e['n'] = (int)$e['n'] + 1;
        $st['k'][$k] = $e;
        return $st;
    });

    jupdate(attempts_file(), function (array $a) use ($now) {
        if ($now - (int)($a['ts'] ?? 0) >= 3600) {
            $a = ['n' => 0, 'ts' => $now];
        }
        $a['n'] = (int)($a['n'] ?? 0) + 1;
        return $a;
    });
}

function attempt_reset(): void
{
    $k = ip_key(client_ip());
    jupdate(attempts_ip_file(), function (array $st) use ($k) {
        if (is_array($st['k'] ?? null)) {
            unset($st['k'][$k]);
        }
        return $st;
    });
}

/**
 * هدرهای پاسخ JSON.
 *
 * CORS فقط برای نقاطِ خواندنی روشن می‌شود. `report.php` که نوشتنی است هرگز
 * CORS نمی‌گیرد تا هیچ صفحهٔ وبی از دامنهٔ دیگر نتواند از طریقِ مرورگرِ کاربر
 * گزارش جعل کند. (کلاینتِ دسکتاپ اصلاً مرورگر نیست و این هدر را نمی‌خواند.)
 */
function api_headers(bool $cors = false): void
{
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store, no-cache, must-revalidate');
    if ($cors) {
        header('Access-Control-Allow-Origin: *');
    }
    header('X-Content-Type-Options: nosniff');
    header('Referrer-Policy: no-referrer');
}

/** هدرهای امنیتی صفحات HTML — قبل از هر خروجی فراخوانی شود. */
function security_headers(): void
{
    if (headers_sent()) {
        return;
    }
    header('X-Content-Type-Options: nosniff');
    header('X-Frame-Options: DENY');
    header('Referrer-Policy: no-referrer');
    header("Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'");
}

/**
 * آیا مسیری داخلِ ریشهٔ داده‌ست؟ (حروف کوچک/بزرگ بی‌تفاوت است: رویِ ویندوز
 * مسیر حساسیت ندارد و ممکن است یکی با شکلِ دیگری ثبت شده باشد.)
 */
function path_is_within(string $path, string $root): bool
{
    $path = rtrim(str_replace('\\', '/', $path), '/');
    $root = rtrim(str_replace('\\', '/', $root), '/');
    if ($path === '' || $root === '') {
        return false;
    }
    if (strcasecmp($path, $root) === 0) {
        return true;
    }
    return strncasecmp($path, $root . '/', strlen($root) + 1) === 0;
}

/**
 * آیا DATA_DIR داخلِ سند-ریشه است؟
 *
 * تنها چیزی که اینجا باید گرفته شود این است که پوشهٔ داده — که auth.json،
 * stats.json، reports.json و .salt در آن است — جایی باشد که وب‌سرور مستقیماً
 * سرو می‌کند. در آن حالت `.htaccess` تنها حفاظتِ رو به وب است و رویِ nginx یا
 * هر میزبانی با `AllowOverride Off` اصلاً اجرا نمی‌شود؛ به‌همین‌دلیل هم پیش‌تر
 * DATA_DIR بیرونِ سند-ریشه رفته، ولی کسی که مهاجرت نکرده باید آن را ببیند.
 *
 * دو ریشه می‌سنجیم:
 *   1) پوشهٔ خودِ این برنامه (server/). طبق README محتوایِ همین پوشه در webroot
 *      می‌نشیند، پس هر مسیری زیرِ آن — مثلِ data/ قدیمی — همان داخلِ webroot است.
 *      این راهِ اصلی است چون به وب‌سرور وابسته نیست.
 *   2) DOCUMENT_ROOT که دقیق‌تر است، ولی وب‌سرور باید آن را به PHP برساند
 *      (nginx گاهی نمی‌رساند). راهِ دومِ مکمل است، نه تنها راه.
 *
 * در CLI که چیزی سرو نمی‌شود «نه» برمی‌گردد.
 */
function data_dir_inside_webroot(): bool
{
    static $memo = null;
    if ($memo !== null) {
        return $memo;
    }

    $raw = (string)DATA_DIR;
    $real = realpath($raw);
    $target = $real !== false ? $real : $raw;
    if ($target === '') {
        return $memo = false;
    }

    $roots = [];
    $self = realpath(__DIR__);
    if ($self !== false) {
        $roots[] = $self;
    }
    $doc = (string)($_SERVER['DOCUMENT_ROOT'] ?? '');
    if ($doc !== '') {
        $d = realpath($doc);
        $roots[] = $d !== false ? $d : $doc;
    }

    foreach ($roots as $r) {
        if (path_is_within($target, $r)) {
            return $memo = true;
        }
    }
    return $memo = false;
}

/**
 * محدودیت نرخ per-IP برای نوشتن آمار؛ بدون آن هر کسی با GUID تصادفی
 * می‌تواند stats.json را بی‌نهها رشد دهد (هر نوشتن = بازنویسی کل فایل).
 */
function rate_allow(string $ip, int $max, int $windowSec): bool
{
    if ($ip === '') {
        return true;
    }
    // IPِ خام ذخیره نمی‌شود: کلید = هشِ نمکی، و فقط تا پایانِ پنجرهٔ
    // نگهداری می‌شود (هر ردیفِ کهنه بلافاصله پاک می‌شود).
    $k = ip_key($ip);
    $f = DATA_DIR . '/ratelimit.json';
    $lock = @fopen(DATA_DIR . '/ratelimit.lock', 'c');
    if ($lock) {
        flock($lock, LOCK_EX);
    }
    try {
        $now = time();
        $st = jread($f, ['k' => []]);
        if (!is_array($st['k'] ?? null)) {
            $st['k'] = [];
        }
        foreach ($st['k'] as $kk => $vv) {
            if (!is_array($vv) || $now - (int)($vv['ts'] ?? 0) >= $windowSec) {
                unset($st['k'][$kk]);
            }
        }
        $e = $st['k'][$k] ?? ['n' => 0, 'ts' => $now];
        if ($now - (int)$e['ts'] >= $windowSec) {
            $e = ['n' => 0, 'ts' => $now];
        }
        if ((int)$e['n'] >= $max) {
            $st['k'][$k] = $e;
            jwrite($f, $st);
            return false;
        }
        $e['n'] = (int)$e['n'] + 1;
        $st['k'][$k] = $e;
        jwrite($f, $st);
        return true;
    } finally {
        if ($lock) {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}

function ver_safe(string $v): string
{
    $v = preg_replace('/[^0-9A-Za-z.\-]/', '', trim($v)) ?? '';
    return substr($v, 0, 32);
}

function lang_ok(?string $lang): string
{
    $lang = strtolower(trim((string)$lang));
    return preg_match('/^[a-z]{2}(-[a-z]{2})?$/', $lang) ? $lang : 'fa';
}

function country_client(): string
{
    $c = strtoupper(preg_replace('/[^A-Za-z]/', '', (string)($_GET['country'] ?? '')) ?? '');
    return preg_match('/^[A-Z]{2}$/', $c) ? $c : '';
}

function country_codes(string $s): array
{
    $out = [];
    foreach (explode(',', $s) as $c) {
        $c = strtoupper(trim($c));
        if (preg_match('/^[A-Z]{2}$/', $c)) {
            $out[] = $c;
        }
    }
    return array_values(array_unique($out));
}

function country_field(): string
{
    return implode(',', country_codes((string)($_POST['countries'] ?? '')));
}

function country_ok(string $list, string $client): bool
{
    $codes = country_codes($list);
    if (!$codes) {
        return true;
    }
    if ($client === '') {
        return false;
    }
    return in_array($client, $codes, true);
}

function langs_all(): array
{
    return ['fa', 'en', 'ar', 'de', 'es', 'fr', 'hi', 'ja', 'ko', 'pt-br', 'ru', 'tr', 'ur', 'zh'];
}

function langs_parse(string $s): array
{
    $out = [];
    foreach (explode(',', $s) as $c) {
        $c = strtolower(trim($c));
        if (in_array($c, langs_all(), true)) {
            $out[] = $c;
        }
    }
    return array_values(array_unique($out));
}

function langs_field(): string
{
    if (isset($_POST['lang_all'])) {
        return '*';
    }
    $sel = $_POST['langs'] ?? [];
    if (!is_array($sel)) {
        $sel = [(string)$sel];
    }
    $codes = [];
    foreach ($sel as $c) {
        $c = strtolower(trim((string)$c));
        if (in_array($c, langs_all(), true)) {
            $codes[] = $c;
        }
    }
    $codes = array_values(array_unique($codes));
    return $codes ? implode(',', $codes) : '*';
}

function item_langs(array $it): array
{
    if (!empty($it['langs'])) {
        $s = (string)$it['langs'];
        return $s === '*' ? [] : langs_parse($s);
    }
    $old = strtolower((string)($it['lang'] ?? '*'));
    return ($old === '*' || $old === '') ? [] : [$old];
}

function lang_matches(array $allowed, string $client): bool
{
    if (!$allowed) {
        return true;
    }
    return in_array(strtolower($client), $allowed, true);
}

function lang_picker(): string
{
    $list = [
        'fa' => 'فارسی', 'en' => 'انگلیسی', 'ar' => 'عربی', 'de' => 'آلمانی',
        'es' => 'اسپانیایی', 'fr' => 'فرانسوی', 'hi' => 'هندی', 'ja' => 'ژاپنی',
        'ko' => 'کره‌ای', 'pt-br' => 'پرتغالی برزیل', 'ru' => 'روسی', 'tr' => 'ترکی',
        'ur' => 'اردو', 'zh' => 'چینی',
    ];
    $html = '<label>زبان‌های هدف (زبان‌های برنامه)</label>';
    $html .= '<div class="check"><input type="checkbox" name="lang_all" id="lang_all" checked onchange="if(this.checked){document.querySelectorAll(\'.langcb\').forEach(function(c){c.checked=false;})}"><label for="lang_all" style="margin:0">همه زبان‌ها</label></div>';
    $html .= '<div style="display:flex;flex-wrap:wrap;gap:4px 16px;margin:4px 0 8px">';
    foreach ($list as $k => $v) {
        $id = 'lg_' . str_replace('-', '_', $k);
        $html .= '<span style="white-space:nowrap"><input type="checkbox" class="langcb" name="langs[]" value="' . $k . '" id="' . $id . '" onchange="if(this.checked){document.getElementById(\'lang_all\').checked=false;}"><label for="' . $id . '" style="margin:0">' . $v . '</label></span>';
    }
    $html .= '</div>';
    return $html;
}

function stats_touch(string $id, array $meta = []): void
{
    if (!preg_match('/^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/', $id)) {
        return;
    }
    $id = strtolower($id);
    $file = DATA_DIR . '/stats.json';
    $lock = @fopen(DATA_DIR . '/stats.lock', 'c');
    if ($lock) {
        flock($lock, LOCK_EX);
    }
    try {
        $st = jread($file, ['clients' => []]);
        if (!is_array($st['clients'] ?? null)) {
            $st['clients'] = [];
        }
        $now = date('c');
        if (isset($st['clients'][$id]) && is_array($st['clients'][$id])) {
            $st['clients'][$id]['last'] = $now;
        } else {
            // سقف ورودی‌ها: GUIDهای جعلی نمی‌توانند فایل را بی‌نهها رشد دهند.
            if (count($st['clients']) >= 5000) {
                return;
            }
            $st['clients'][$id] = ['first' => $now, 'last' => $now];
        }
        foreach ($meta as $k => $v) {
            if ($v === null || $v === '') {
                continue;
            }
            $st['clients'][$id][$k] = $v;
        }
        // پاک‌سازیِ برچسبِ منبعِ کلاینت‌هایی که دیگر سر نزده‌اند. خودِ رکورد می‌ماند؛
        // فقط هشِ منبع می‌رود. ردیفِ همین درخواست‌دهنده تازه‌ترین است (last همین حالا
        // به now رسید)، پس برچسبِ او هیچ‌وقت در همین عبور پاک نمی‌شود.
        foreach ($st['clients'] as &$row) {
            if (!is_array($row)) {
                continue;
            }
            $hasSrc = (($row['ip_last'] ?? '') !== '') || (($row['ip'] ?? '') !== '');
            if ($hasSrc && src_hash_expired((string)($row['last'] ?? ''))) {
                $row['ip_last'] = '';
                $row['ip'] = '';
            }
        }
        unset($row);
        jwrite($file, $st);
    } finally {
        if ($lock) {
            flock($lock, LOCK_UN);
            fclose($lock);
        }
    }
}

function stats_find(string $id): ?array
{
    $id = strtolower(trim($id));
    if (!preg_match('/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/', $id)) {
        return null;
    }
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $c = $st['clients'][$id] ?? null;
    if (!is_array($c)) {
        return null;
    }
    $c['id'] = $id;
    return $c;
}

function stats_sorted(): array
{
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $out = [];
    foreach (($st['clients'] ?? []) as $id => $c) {
        if (!is_array($c)) {
            continue;
        }
        $c['id'] = (string)$id;
        $out[] = $c;
    }
    usort($out, function ($a, $b) {
        return strcmp((string)($b['last'] ?? ''), (string)($a['last'] ?? ''));
    });
    return $out;
}

function stats_summary(): array
{
    $st = jread(DATA_DIR . '/stats.json', ['clients' => []]);
    $now = time();
    $installs = 0;
    $a24 = 0;
    $a7 = 0;
    $online = 0;
    $conns = 0;
    foreach (($st['clients'] ?? []) as $c) {
        if (!is_array($c)) {
            continue;
        }
        $installs++;
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
    return [
        'installs' => $installs,
        'active24' => $a24,
        'active7' => $a7,
        'online' => $online,
        'connections' => $conns,
    ];
}

function stats_online(array $c, int $window = 1200): bool
{
    $t = strtotime((string)($c['last'] ?? '')) ?: 0;
    return $t > 0 && time() - $t <= $window;
}

function ip_public(string $ip): bool
{
    if (filter_var($ip, FILTER_VALIDATE_IP) === false) {
        return false;
    }
    return filter_var($ip, FILTER_VALIDATE_IP, FILTER_FLAG_NO_PRIV_RANGE | FILTER_FLAG_NO_RES_RANGE) !== false;
}

function ip_info(string $ip): ?array
{
    if (!ip_public($ip)) {
        return ['isp' => '', 'asn' => 0, 'org' => ''];
    }
    $ctx = stream_context_create([
        'http' => [
            'timeout' => 3,
            'header' => "User-Agent: RoxaPanel/1.0\r\nAccept: application/json\r\n",
            'ignore_errors' => true,
        ],
    ]);
    $raw = @file_get_contents('https://ipwho.is/' . rawurlencode($ip), false, $ctx);
    if ($raw === false || $raw === '') {
        return null;
    }
    $j = json_decode($raw, true);
    if (!is_array($j)) {
        return null;
    }
    if (empty($j['success'])) {
        return ['isp' => '', 'asn' => 0, 'org' => ''];
    }
    $conn = is_array($j['connection'] ?? null) ? $j['connection'] : [];
    $isp = trim((string)($conn['isp'] ?? ''));
    $org = trim((string)($conn['org'] ?? ''));
    $asn = (int)($conn['asn'] ?? 0);
    return ['isp' => str_cut($isp, 80), 'asn' => max(0, $asn), 'org' => str_cut($org, 80)];
}

function ip_info_save(string $id, array $info): void
{
    stats_touch($id, [
        'isp_done' => 1,
        'isp' => $info['isp'] !== '' ? $info['isp'] : null,
        'asn' => $info['asn'] > 0 ? $info['asn'] : null,
        'org' => $info['org'] !== '' ? $info['org'] : null,
    ]);
}

/** سقف ساعتی فراخوانی ipwho.is؛ بدون آن هر شناسهٔ جعلیِ `id` یک درخواست سمت‌سرور می‌سازد. */
function ipinfo_allow(): bool
{
    $f = DATA_DIR . '/ipinfo_quota.json';
    $q = jread($f, ['h' => '', 'n' => 0]);
    $h = gmdate('YmdH');
    if ((string)($q['h'] ?? '') !== $h) {
        $q = ['h' => $h, 'n' => 0];
    }
    if ((int)($q['n'] ?? 0) >= 60) {
        return false;
    }
    $q['n'] = (int)($q['n'] ?? 0) + 1;
    jwrite($f, $q);
    return true;
}

/**
 * غنی‌سازیِ ISP/ASN برای ردیف‌هایِ تکمیل‌نشده. عمداً با IPِ ذخیره‌شده کار می‌کند،
 * و چون IPِ خام دیگر ذخیره نمی‌شود، برایِ ردیف‌هایِ جدید این تابع کاری نمی‌کند —
 * اطلاعاتِ ISP هنگامِ check.php و با IPِ در حافظه ثبت می‌شود. برایِ دادهٔ
 * قدیمیِ ذخیره‌شده همچنان کار می‌کند.
 */
function ip_fill(array &$rows, int $cap = 10, float $budget = 4.0): void
{
    $deadline = microtime(true) + $budget;
    $done = 0;
    foreach ($rows as &$c) {
        if ($done >= $cap || microtime(true) >= $deadline) {
            break;
        }
        $id = (string)($c['id'] ?? '');
        $ip = (string)($c['ip'] ?? '');
        if ($id === '' || !empty($c['isp_done'])) {
            continue;
        }
        if ($ip === '') {
            continue;
        }
        if (!ip_public($ip)) {
            stats_touch($id, ['isp_done' => 1]);
            $c['isp_done'] = 1;
            continue;
        }
        $info = ip_info($ip);
        if ($info === null) {
            continue;
        }
        ip_info_save($id, $info);
        $c['isp_done'] = 1;
        $c['isp'] = $info['isp'];
        $c['asn'] = $info['asn'];
        $c['org'] = $info['org'];
        $done++;
    }
    unset($c);
}

/**
 * فقط https. لینک‌های `javascript:`، `data:`، `vbscript:` و `http:` پذیرفته
 * نمی‌شوند؛ خروجیِ خالی یعنی «لینکِ خالیِ مجاز»، null یعنی «نامعتبر».
 */
function safe_url(string $url): ?string
{
    $url = trim($url);
    if ($url === '') {
        return '';
    }
    if (!preg_match('#^https://[^\s<>"\']+$#i', $url)) {
        return null;
    }
    $p = parse_url($url);
    if (!is_array($p) || empty($p['host'])) {
        return null;
    }
    return $url;
}

/**
 * نمایشِ کوتاهِ «کلیدِ منبع» در پنل. این دیگر IP نیست؛ نمی‌شود آن را به آدرسِ
 * واقعی برگرداند، ولی همچنان می‌گوید «همان منسابِ قبلی» یا «منبعی دیگر».
 */
function src_short(string $src): string
{
    return $src === '' ? '' : substr($src, 0, 12);
}

function str_cut(string $s, int $len): string
{
    if (function_exists('mb_substr')) {
        return mb_substr($s, 0, $len);
    }
    if (preg_match('/^.{0,' . $len . '}/us', $s, $m)) {
        return $m[0];
    }
    return substr($s, 0, $len);
}

function str_len(string $s): int
{
    if (function_exists('mb_strlen')) {
        return mb_strlen($s);
    }
    $n = preg_match_all('/./us', $s);
    return $n === false ? strlen($s) : $n;
}
