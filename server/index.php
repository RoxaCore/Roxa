<?php
$ver = ['latest' => '', 'file' => '', 'size' => 0, 'sha256' => ''];
$vj = __DIR__ . '/data/version.json';
if (is_file($vj)) {
    $j = json_decode((string)@file_get_contents($vj), true);
    if (is_array($j)) {
        foreach ($ver as $k => $dv) {
            if (isset($j[$k])) {
                $ver[$k] = $j[$k];
            }
        }
    }
}
$e = static function ($s) { return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8'); };
// Downloads are counted by api/dl.php (a 302 to the same file); '#' stays the
// placeholder when no file has been published yet.
$dlUrl = $ver['file'] !== '' ? 'api/dl.php?f=' . rawurlencode((string)$ver['file']) : '#download';
$mb = $ver['size'] ? number_format(((int)$ver['size']) / 1048576, 1) : '';

// ---- language ----
if (isset($_GET['lang']) && ($_GET['lang'] === 'en' || $_GET['lang'] === 'fa')) {
    $lang = $_GET['lang'];
} else {
    $al = (string)($_SERVER['HTTP_ACCEPT_LANGUAGE'] ?? '');
    $lang = (stripos($al, 'en') === 0) ? 'en' : 'fa';
}
$isEn = $lang === 'en';
$dir = $isEn ? 'ltr' : 'rtl';
$switchUrl = $isEn ? '?lang=fa' : '?lang=en';
$switchLabel = $isEn ? 'فارسی' : 'EN';

$ICONS = [
    'bolt'   => '<path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z"/>',
    'layers' => '<polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/>',
    'clock'  => '<circle cx="12" cy="12" r="10"/><polyline points="12 6 12 12 16 14"/>',
    'shield' => '<path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>',
    'sync'   => '<polyline points="23 4 23 10 17 10"/><path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10"/>',
    'bell'   => '<path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/><path d="M13.73 21a2 2 0 0 1-3.46 0"/>',
    'zip'    => '<path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/>',
    'globe'  => '<circle cx="12" cy="12" r="10"/><line x1="2" y1="12" x2="22" y2="12"/><path d="M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z"/>',
];

$T = $isEn ? [
    'meta_title' => 'Roxa — Fast and secure connection client for Windows',
    'meta_desc' => 'Roxa is a modern Windows client built on the Xray core: VLESS, VMess, Trojan and Shadowsocks, real latency testing, TUN mode with kill switch and automatic updates.',
    'nav_features' => 'Features',
    'nav_how' => 'How it works',
    'nav_faq' => 'FAQ',
    'nav_download' => 'Download %s',
    'badge' => 'Version %s ready to download',
    'h1' => 'Fast and secure connection,<br><span class="g">in one click</span>',
    'lead' => 'Roxa is a modern client for Windows built on the Xray core — it manages your subscriptions, finds the fastest server and connects without the hassle.',
    'cta_download' => 'Download for Windows',
    'cta_how' => 'How does it work?',
    'cta_meta' => 'Size <b>%s MB</b> · Version <b>%s</b> · No installation required',
    'strip' => [
        ['Xray core', 'Popular high-speed core'],
        ['14 languages', 'Persian, English and 12 more'],
        ['TUN + Kill Switch', 'No traffic leaks'],
        ['Auto updates', 'New versions inside the app'],
    ],
    'f_kicker' => 'Features',
    'f_h2' => 'Everything you need for a good connection',
    'f_sub' => 'From adding a subscription to automatic server switching — all in one lightweight Windows app.',
    'features' => [
        ['bolt', 'One-click connect', 'Add your subscription or server link, hit connect — Roxa handles the rest.'],
        ['layers', 'Wide protocol support', 'VLESS, VMess, Trojan and Shadowsocks, plus common subscription links.'],
        ['clock', 'Real latency & auto-switch', 'Latency tested from inside the tunnel, with automatic selection of the fastest server when things slow down.'],
        ['shield', 'TUN mode & kill switch', 'All app traffic goes through the tunnel; if the connection drops, the internet is cut too — no leaks.'],
        ['sync', 'Automatic updates', 'New releases are announced inside the app and installed in a couple of clicks.'],
        ['bell', 'Notifications & bug reports', 'See important notices and report issues in one click — crashes are sent automatically.'],
        ['zip', 'Portable & lightweight', 'Runs straight from a ZIP — no installer needed, just unzip and start.'],
        ['globe', 'Multilingual UI', '14 languages from Persian and English to Arabic, Turkish, Urdu and Chinese — with full RTL support.'],
    ],
    'h_kicker' => 'Quick start',
    'h_h2' => 'Connected in three steps',
    'h_sub' => 'No long install, no complex settings — from download to connection in under a minute.',
    'steps' => [
        ['Download & run', 'Download the ZIP, open it and run Roxa — no installation needed.'],
        ['Add subscription or link', "Paste your provider's subscription link, or add a direct server link (VLESS/VMess/Trojan/SS)."],
        ['Connect in one click', 'Hit connect; if needed, a better server is picked automatically.'],
    ],
    'd_kicker' => 'Download',
    'd_h2' => 'Roxa for Windows',
    'd_desc' => 'The stable build for Windows 10 and 11 (64-bit). Unzip and run — no installer.',
    'd_btn' => 'Download version %s',
    'd_version' => 'Version',
    'd_size' => 'Size',
    'd_os' => 'Operating system',
    'd_format' => 'Format',
    'd_format_v' => 'ZIP — portable',
    'd_sha' => 'SHA-256',
    'q_kicker' => 'FAQ',
    'q_h2' => 'Before you download',
    'faq' => [
        ['Is Roxa free?', 'Yes, the app itself is free. Roxa is a connection tool — you need a subscription or server link from your own provider.'],
        ['Where do I add my subscription?', 'In the Subscriptions section, paste your provider\'s subscription link to fetch and refresh the server list automatically. Direct VLESS/VMess/Trojan/Shadowsocks links are also supported.'],
        ['How are updates delivered?', 'The app checks for new versions and notifies you; download and install in a couple of clicks. The latest release is always available on this page too.'],
        ['Which operating systems are supported?', 'Windows 10 and 11, 64-bit. Other platforms are being considered for future releases.'],
    ],
    'foot_copy' => '© 2026 Roxa — up.roxacore.com',
    'foot_features' => 'Features',
    'foot_how' => 'Quick start',
    'foot_faq' => 'FAQ',
    'foot_download' => 'Download',
    'mock_bar' => 'Roxa — Connected',
    'mock_conn' => '● Connected — Germany 1',
    'mock_btn' => 'Connect in one click',
    'mock_servers' => [
        ['Netherlands — Amsterdam', '86 ms', 'g'],
        ['Germany — Frankfurt', '132 ms', 'g'],
        ['France — Paris', '211 ms', 'y'],
        ['England — London', '104 ms', 'g'],
    ],
    'mock_conn_ms' => '132 ms',
] : [
    'meta_title' => 'Roxa — کلاینت اتصال سریع و امن برای ویندوز',
    'meta_desc' => 'Roxa یک کلاینت مدرن برای ویندوز با هستهٔ Xray است: پشتیبانی از VLESS و VMess و Trojan و Shadowsocks، تست تاخیر واقعی، حالت TUN با کیل‌سوییچ و آپدیت خودکار.',
    'nav_features' => 'امکانات',
    'nav_how' => 'چطور کار می‌کند',
    'nav_faq' => 'سوالات',
    'nav_download' => 'دانلود %s',
    'badge' => 'نسخهٔ %s آمادهٔ دانلود',
    'h1' => 'اتصال سریع و امن،<br><span class="g">با یک کلیک</span>',
    'lead' => 'Roxa یک کلاینت مدرن برای ویندوز است که با هستهٔ Xray کار می‌کند؛ اشتراک‌ها را مدیریت می‌کند، سریع‌ترین سرور را پیدا می‌کند و بدون پیچیدگی وصل می‌شود.',
    'cta_download' => 'دانلود برای ویندوز',
    'cta_how' => 'چطور کار می‌کند؟',
    'cta_meta' => 'حجم <b>%s مگابایت</b> · نسخه <b>%s</b> · بدون نیاز به نصب',
    'strip' => [
        ['Xray', 'هستهٔ محبوب و پرسرعت'],
        ['۱۴ زبان', 'فارسی، انگلیسی و ۱۲ زبان دیگر'],
        ['TUN + Kill Switch', 'بدون نشت ترافیک'],
        ['آپدیت خودکار', 'نسخهٔ جدید داخل برنامه'],
    ],
    'f_kicker' => 'امکانات',
    'f_h2' => 'همه‌چیزی که برای یک اتصال خوب لازم دارید',
    'f_sub' => 'از افزودن اشتراک تا سوییچ خودکار بین سرورها — همه در یک برنامهٔ سبک برای ویندوز.',
    'features' => [
        ['bolt', 'اتصال با یک کلیک', 'اشتراک یا لینک سرور را اضافه کنید و دکمهٔ اتصال را بزنید؛ بقیهٔ کارها را Roxa انجام می‌دهد.'],
        ['layers', 'پروتکل‌های متنوع', 'VLESS، VMess، Trojan و Shadowsocks به‌همراه لینک اشتراک (Subscription) سرویس‌های رایج.'],
        ['clock', 'تاخیر واقعی و سوییچ خودکار', 'تست تاخیر به‌صورت واقعی از داخل تونل و انتخاب خودکار سریع‌ترین سرور وقتی اتصال کند شد.'],
        ['shield', 'حالت TUN و کیل‌سوییچ', 'ترافیک تمام برنامه‌ها از تونل عبور می‌کند و اگر اتصال قطع شد، اینترنت هم قطع می‌شود تا نشتی رخ ندهد.'],
        ['sync', 'آپدیت خودکار', 'انتشار نسخهٔ جدید داخل خود برنامه اطلاع‌رسانی می‌شود و با چند کلیک نصب می‌گردد.'],
        ['bell', 'اعلان‌ها و گزارش باگ', 'اعلان‌های مهم را ببینید و اگر مشکلی دیدید با یک کلیک گزارش دهید — کرش‌ها هم خودکار ارسال می‌شوند.'],
        ['zip', 'قابل حمل و سبک', 'مستقیماً از داخل ZIP اجرا می‌شود؛ بدون فرآیند نصب، فقط باز کنید و اجرا کنید.'],
        ['globe', 'رابط چندزبانه', '۱۴ زبان از فارسی و انگلیسی تا عربی، ترکی، اردو و چینی — با رابط راست‌به‌چپ کامل.'],
    ],
    'h_kicker' => 'شروع سریع',
    'h_h2' => 'در سه قدم وصل شوید',
    'h_sub' => 'نه نصب طولانی، نه تنظیمات پیچیده — از دانلود تا اتصال در کمتر از یک دقیقه.',
    'steps' => [
        ['دانلود و اجرا', 'فایل ZIP را دانلود کنید، باز کنید و Roxa را اجرا کنید — بدون نیاز به نصب.'],
        ['افزودن اشتراک یا لینک', 'لینک اشتراک سرویس خود را وارد کنید یا مستقیماً لینک سرور (VLESS/VMess/Trojan/SS) را اضافه کنید.'],
        ['اتصال با یک کلیک', 'دکمهٔ اتصال را بزنید؛ در صورت نیاز، سرور بهتر به‌صورت خودکار انتخاب می‌شود.'],
    ],
    'd_kicker' => 'دانلود',
    'd_h2' => 'Roxa برای ویندوز',
    'd_desc' => 'نسخهٔ پایدار با پشتیبانی از ویندوز ۱۰ و ۱۱ (۶۴ بیت). فایل فشرده را باز کنید و بدون نصب اجرا کنید.',
    'd_btn' => 'دانلود نسخهٔ %s',
    'd_version' => 'نسخه',
    'd_size' => 'حجم',
    'd_os' => 'سیستم‌عامل',
    'd_format' => 'فرمت',
    'd_format_v' => 'ZIP — بدون نصب',
    'd_sha' => 'SHA-256',
    'q_kicker' => 'سوالات متداول',
    'q_h2' => 'قبل از دانلود بدانید',
    'faq' => [
        ['آیا Roxa رایگان است؟', 'بله، خود برنامه رایگان است. Roxa ابزار اتصال است؛ برای استفاده به لینک اشتراک یا لینک سرور سرویسی نیاز دارید که خودتان دارید (مانند اشتراک سرویس‌دهندهٔ شما).'],
        ['اشتراک را از کجا وارد کنم؟', 'در بخش اشتراک‌ها، لینک اشتراک (Subscription) سرویس‌دهندهٔ خود را وارد کنید تا فهرست سرورها به‌صورت خودکار دریافت و به‌روزرسانی شود. لینک‌های مستقیم VLESS/VMess/Trojan/Shadowsocks هم پشتیبانی می‌شوند.'],
        ['آپدیت‌ها چطور انجام می‌شود؟', 'برنامه خودش نسخهٔ جدید را چک می‌کند و اعلان می‌دهد؛ با چند کلیک دانلود و نصب می‌شود. نسخهٔ جدید همیشه از همین صفحه قابل دریافت مستقیم است.'],
        ['چه سیستم‌عامل‌هایی پشتیبانی می‌شود؟', 'فعلاً ویندوز ۱۰ و ۱۱ در نسخهٔ ۶۴ بیت. نسخه‌های دیگر سیستم‌عامل در برنامه‌های آینده بررسی می‌شوند.'],
    ],
    'foot_copy' => '© ۲۰۲۶ Roxa — up.roxacore.com',
    'foot_features' => 'امکانات',
    'foot_how' => 'شروع سریع',
    'foot_faq' => 'سوالات',
    'foot_download' => 'دانلود',
    'mock_bar' => 'Roxa — اتصال برقرار است',
    'mock_conn' => '● متصل — سرور آلمان ۱',
    'mock_btn' => 'اتصال با یک کلیک',
    'mock_servers' => [
        ['هلند — آمستردام', '86 ms', 'g'],
        ['آلمان — فرانکفورت', '132 ms', 'g'],
        ['فرانسه — پاریس', '211 ms', 'y'],
        ['انگلیس — لندن', '104 ms', 'g'],
    ],
    'mock_conn_ms' => '132 ms',
];

$vTag = $ver['latest'] !== '' ? sprintf($T['badge'], $ver['latest']) : ($isEn ? 'New version' : 'نسخهٔ جدید');
$dlBtnLabel = sprintf($T['d_btn'], $ver['latest'] !== '' ? $ver['latest'] : ($isEn ? 'latest' : 'جدید'));
$navDlLabel = sprintf($T['nav_download'], $ver['latest'] ?? '');
$ctaMeta = ($mb !== '') ? sprintf($T['cta_meta'], $mb, $ver['latest']) : '';
$shaShort = $ver['sha256'] !== '' ? substr((string)$ver['sha256'], 0, 16) . '…' : '';
?>
<!doctype html>
<html lang="<?= $lang ?>" dir="<?= $dir ?>">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title><?= $e($T['meta_title']) ?></title>
<meta name="description" content="<?= $e($T['meta_desc']) ?>">
<link rel="canonical" href="https://up.roxacore.com/">
<meta property="og:type" content="website">
<meta property="og:title" content="<?= $e($T['meta_title']) ?>">
<meta property="og:description" content="<?= $e($T['meta_desc']) ?>">
<link href="/fonts/vazirmatn.css" rel="stylesheet">
<style>
:root{
  --bg:#070a12;--bg2:#0b0f1a;--card:#111827;--card2:#0d1322;--line:#1f2937;
  --tx:#e5e7eb;--mut:#9ca3af;--ac:#7c3aed;--ac2:#a78bfa;--ok:#10b981;--ring:rgba(124,58,237,.35);
}
*{box-sizing:border-box;margin:0;padding:0}
html{scroll-behavior:smooth}
body{background:var(--bg);color:var(--tx);font-family:Vazirmatn,Tahoma,sans-serif;line-height:1.9;font-size:15px;overflow-x:hidden}
a{color:inherit;text-decoration:none}
.wrap{max-width:1120px;margin:0 auto;padding:0 22px}
.mut{color:var(--mut)}
.mono{direction:ltr;display:inline-block;font-family:Consolas,Menlo,monospace;font-size:12px}

/* ---------- nav ---------- */
.nav{position:sticky;top:0;z-index:50;background:rgba(7,10,18,.82);backdrop-filter:blur(10px);border-bottom:1px solid var(--line)}
.nav .inner{display:flex;align-items:center;justify-content:space-between;height:64px;gap:18px}
.brand{display:flex;align-items:center;gap:10px;font-weight:800;font-size:19px;letter-spacing:.2px}
.logo{width:32px;height:32px;border-radius:9px;display:grid;place-items:center;font-size:16px;font-weight:900;color:#fff;background:linear-gradient(135deg,var(--ac),#4f46e5);box-shadow:0 4px 16px var(--ring)}
.nav-links{display:flex;gap:26px;font-size:14px;color:var(--mut);align-items:center}
.nav-links a:hover{color:#fff}
.lang{display:inline-flex;border:1px solid var(--line);border-radius:999px;overflow:hidden;background:var(--card);font-size:12px;font-weight:700}
.lang a{padding:4px 12px;color:var(--mut);transition:.2s}
.lang a:hover{color:#fff}
.lang a.on{background:rgba(124,58,237,.25);color:#fff}
.btn{display:inline-flex;align-items:center;gap:8px;border-radius:10px;padding:9px 20px;font-family:inherit;font-size:14px;font-weight:700;cursor:pointer;border:0;transition:.2s}
.btn-p{background:linear-gradient(135deg,var(--ac),#4f46e5);color:#fff;box-shadow:0 6px 22px var(--ring)}
.btn-p:hover{transform:translateY(-1px);box-shadow:0 10px 28px var(--ring)}
.btn-g{background:var(--card);color:var(--tx);border:1px solid var(--line)}
.btn-g:hover{border-color:var(--ac2)}
.btn-lg{padding:14px 30px;font-size:16px;border-radius:12px}
.nav .btn{font-size:13px;padding:8px 16px;white-space:nowrap}

/* ---------- hero ---------- */
.hero{position:relative;padding:88px 0 70px;overflow:hidden}
.hero::before{content:"";position:absolute;top:-260px;left:-180px;width:640px;height:640px;border-radius:50%;background:radial-gradient(circle,rgba(124,58,237,.22),transparent 65%);pointer-events:none}
.hero::after{content:"";position:absolute;bottom:-300px;right:-200px;width:620px;height:620px;border-radius:50%;background:radial-gradient(circle,rgba(79,70,229,.16),transparent 65%);pointer-events:none}
.hero .grid{display:grid;grid-template-columns:1.05fr .95fr;gap:52px;align-items:center;position:relative}
.badge{display:inline-flex;align-items:center;gap:8px;background:rgba(124,58,237,.14);border:1px solid rgba(167,139,250,.4);color:var(--ac2);border-radius:999px;padding:5px 14px;font-size:13px;font-weight:700}
.badge i{width:8px;height:8px;border-radius:50%;background:var(--ok);box-shadow:0 0 0 0 rgba(16,185,129,.6);animation:pulse 2s infinite}
@keyframes pulse{70%{box-shadow:0 0 0 9px rgba(16,185,129,0)}100%{box-shadow:0 0 0 0 rgba(16,185,129,0)}}
h1{font-size:44px;line-height:1.35;font-weight:900;margin:20px 0 16px}
h1 .g{background:linear-gradient(90deg,var(--ac2),#60a5fa);-webkit-background-clip:text;background-clip:text;color:transparent}
.lead{font-size:17px;color:var(--mut);max-width:560px}
.cta{display:flex;gap:14px;margin-top:30px;flex-wrap:wrap;align-items:center}
.cta .meta{font-size:12.5px;color:var(--mut)}
.cta .meta b{color:var(--tx)}

/* app mockup */
.mock{background:linear-gradient(180deg,#111827,#0b1220);border:1px solid var(--line);border-radius:16px;box-shadow:0 30px 80px rgba(0,0,0,.55),0 0 0 1px rgba(167,139,250,.08);overflow:hidden;transform:perspective(1400px) rotateY(-6deg) rotateX(2deg)}
[dir=ltr] .mock{transform:perspective(1400px) rotateY(6deg) rotateX(2deg)}
.mock .bar{display:flex;align-items:center;gap:7px;padding:11px 14px;border-bottom:1px solid var(--line);background:#0d1322}
.mock .dot{width:11px;height:11px;border-radius:50%}
.mock .bar span{font-size:11px;color:var(--mut);margin-inline-start:8px}
.mock .body{display:grid;grid-template-columns:64px 1fr;min-height:320px}
.mock .side{background:#0a0f1c;border-inline-end:1px solid var(--line);padding:14px 0;display:flex;flex-direction:column;align-items:center;gap:14px}
.mock .sq{width:30px;height:30px;border-radius:8px;background:#151c30;display:grid;place-items:center;color:var(--mut);font-size:13px}
.mock .sq.on{background:linear-gradient(135deg,var(--ac),#4f46e5);color:#fff}
.mock .list{padding:14px 16px;display:flex;flex-direction:column;gap:10px}
.mock .conn{display:flex;align-items:center;justify-content:space-between;background:rgba(16,185,129,.1);border:1px solid rgba(16,185,129,.35);border-radius:10px;padding:10px 14px;font-size:13px;color:#6ee7b7}
.mock .row{display:flex;align-items:center;justify-content:space-between;background:var(--card2);border:1px solid var(--line);border-radius:10px;padding:10px 14px;font-size:13px;transition:.2s}
.mock .row:hover{border-color:var(--ac2)}
.mock .pill{font-size:11px;border-radius:999px;padding:2px 9px;background:#151c30;color:var(--mut);direction:ltr}
.mock .pill.g{color:#6ee7b7;background:rgba(16,185,129,.12)}
.mock .pill.y{color:#fcd34d;background:rgba(245,158,11,.12)}
.mock .btnf{margin-top:4px;background:linear-gradient(135deg,var(--ac),#4f46e5);border-radius:10px;text-align:center;padding:11px;font-size:14px;font-weight:700;color:#fff}

/* ---------- strip ---------- */
.strip{border-block:1px solid var(--line);background:var(--bg2)}
.strip .inner{display:grid;grid-template-columns:repeat(4,1fr);gap:12px;padding:22px 0;text-align:center}
.strip b{display:block;font-size:20px;font-weight:900;color:#fff}
.strip span{font-size:12.5px;color:var(--mut)}

/* ---------- sections ---------- */
section{padding:78px 0}
.kicker{color:var(--ac2);font-size:13.5px;font-weight:800;letter-spacing:.5px}
h2{font-size:31px;font-weight:900;margin:8px 0 14px}
.sub{color:var(--mut);max-width:640px;margin-bottom:40px}
.feat{display:grid;grid-template-columns:repeat(4,1fr);gap:16px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:22px;transition:.25s;position:relative;overflow:hidden}
.card::after{content:"";position:absolute;inset:0;background:linear-gradient(160deg,rgba(124,58,237,.14),transparent 45%);opacity:0;transition:.25s}
.card:hover{transform:translateY(-4px);border-color:rgba(167,139,250,.5);box-shadow:0 16px 40px rgba(0,0,0,.4)}
.card:hover::after{opacity:1}
.ic{width:42px;height:42px;border-radius:11px;display:grid;place-items:center;background:rgba(124,58,237,.16);border:1px solid rgba(167,139,250,.35);color:var(--ac2);margin-bottom:14px}
.ic svg{width:21px;height:21px;stroke:currentColor;fill:none;stroke-width:1.8;stroke-linecap:round;stroke-linejoin:round}
.card h3{font-size:15.5px;margin-bottom:7px}
.card p{font-size:13px;color:var(--mut);line-height:1.85}

/* steps */
.steps{display:grid;grid-template-columns:repeat(3,1fr);gap:18px}
.step{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:26px 22px;position:relative}
.step .n{width:36px;height:36px;border-radius:50%;display:grid;place-items:center;font-weight:900;color:#fff;background:linear-gradient(135deg,var(--ac),#4f46e5);margin-bottom:14px;box-shadow:0 6px 18px var(--ring)}
.step h3{font-size:16px;margin-bottom:7px}
.step p{font-size:13.5px;color:var(--mut)}

/* download */
.dlcard{background:var(--card);border:1px solid var(--line);border-radius:18px;padding:34px;display:grid;grid-template-columns:1.2fr .8fr;gap:34px;align-items:center;box-shadow:0 24px 70px rgba(0,0,0,.45)}
.dlcard h2{margin-top:0}
.spec{background:var(--card2);border:1px solid var(--line);border-radius:13px;padding:18px 20px;display:flex;flex-direction:column;gap:11px;font-size:13.5px}
.spec .r{display:flex;justify-content:space-between;gap:12px}
.spec .r b{font-weight:700;color:#fff}
.spec .r span{color:var(--mut);direction:ltr;text-align:left}

/* faq */
.faq{display:flex;flex-direction:column;gap:12px;max-width:820px}
details{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:0 20px;transition:.2s}
details[open]{border-color:rgba(167,139,250,.45)}
summary{cursor:pointer;font-weight:700;font-size:14.5px;padding:17px 0;list-style:none;display:flex;justify-content:space-between;align-items:center;gap:10px}
summary::-webkit-details-marker{display:none}
summary::after{content:"+";color:var(--ac2);font-size:20px;font-weight:400;transition:.2s}
details[open] summary::after{transform:rotate(45deg)}
details p{color:var(--mut);font-size:13.5px;padding:0 0 18px}

/* footer */
footer{border-top:1px solid var(--line);background:var(--bg2);padding:34px 0;color:var(--mut);font-size:13px}
footer .inner{display:flex;justify-content:space-between;gap:14px;flex-wrap:wrap;align-items:center}
footer .fl{display:flex;gap:20px}
footer a:hover{color:#fff}

.reveal{opacity:0;transform:translateY(22px);transition:.6s cubic-bezier(.2,.7,.2,1)}
.reveal.on{opacity:1;transform:none}

@media (max-width:980px){
  .hero .grid{grid-template-columns:1fr;gap:38px}
  h1{font-size:35px}
  .feat{grid-template-columns:repeat(2,1fr)}
  .steps{grid-template-columns:1fr}
  .dlcard{grid-template-columns:1fr}
  .strip .inner{grid-template-columns:repeat(2,1fr)}
  .nav-links a{display:none}
  .nav-links .lang{display:inline-flex}
  .mock{transform:none !important}
}
@media (max-width:560px){
  .feat{grid-template-columns:1fr}
  h1{font-size:29px}
  section{padding:58px 0}
}
@media (prefers-reduced-motion:reduce){
  .reveal{opacity:1;transform:none;transition:none}
  html{scroll-behavior:auto}
}
</style>
</head>
<body>

<header class="nav">
  <div class="wrap inner">
    <a class="brand" href="#"><span class="logo">R</span> Roxa</a>
    <nav class="nav-links">
      <a href="#features"><?= $e($T['nav_features']) ?></a>
      <a href="#how"><?= $e($T['nav_how']) ?></a>
      <a href="#faq"><?= $e($T['nav_faq']) ?></a>
      <span class="lang"><a href="?lang=fa" class="<?= $isEn ? '' : 'on' ?>">FA</a><a href="?lang=en" class="<?= $isEn ? 'on' : '' ?>">EN</a></span>
    </nav>
    <a class="btn btn-p" href="<?= $e($dlUrl) ?>"><?= $e($navDlLabel) ?></a>
  </div>
</header>

<section class="hero">
  <div class="wrap grid">
    <div>
      <span class="badge"><i></i> <?= $e($vTag) ?></span>
      <h1><?= $T['h1'] ?></h1>
      <p class="lead"><?= $e($T['lead']) ?></p>
      <div class="cta">
        <a class="btn btn-p btn-lg" href="<?= $e($dlUrl) ?>">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>
          <?= $e($T['cta_download']) ?>
        </a>
        <a class="btn btn-g btn-lg" href="#how"><?= $e($T['cta_how']) ?></a>
        <?php if ($ctaMeta !== ''): ?><span class="meta"><?= $ctaMeta ?></span><?php endif; ?>
      </div>
    </div>

    <div class="mock" aria-hidden="true">
      <div class="bar">
        <span class="dot" style="background:#ef4444"></span>
        <span class="dot" style="background:#f59e0b"></span>
        <span class="dot" style="background:#10b981"></span>
        <span><?= $e($T['mock_bar']) ?></span>
      </div>
      <div class="body">
        <div class="side">
          <div class="sq on">R</div>
          <div class="sq">⌘</div>
          <div class="sq">☰</div>
          <div class="sq">⚙</div>
        </div>
        <div class="list">
          <div class="conn"><span><?= $e($T['mock_conn']) ?></span><span class="pill g"><?= $e($T['mock_conn_ms']) ?></span></div>
          <?php foreach ($T['mock_servers'] as $s): ?>
          <div class="row"><span><?= $e($s[0]) ?></span><span class="pill <?= $e($s[2]) ?>"><?= $e($s[1]) ?></span></div>
          <?php endforeach; ?>
          <div class="btnf"><?= $e($T['mock_btn']) ?></div>
        </div>
      </div>
    </div>
  </div>
</section>

<div class="strip">
  <div class="wrap inner">
    <?php foreach ($T['strip'] as $s): ?>
    <div><b><?= $e($s[0]) ?></b><span><?= $e($s[1]) ?></span></div>
    <?php endforeach; ?>
  </div>
</div>

<section id="features">
  <div class="wrap">
    <div class="reveal">
      <div class="kicker"><?= $e($T['f_kicker']) ?></div>
      <h2><?= $e($T['f_h2']) ?></h2>
      <p class="sub"><?= $e($T['f_sub']) ?></p>
    </div>
    <div class="feat">
      <?php foreach ($T['features'] as $f): ?>
      <div class="card reveal">
        <div class="ic"><svg viewBox="0 0 24 24"><?= $ICONS[$f[0]] ?></svg></div>
        <h3><?= $e($f[1]) ?></h3>
        <p><?= $e($f[2]) ?></p>
      </div>
      <?php endforeach; ?>
    </div>
  </div>
</section>

<section id="how" style="padding-top:0">
  <div class="wrap">
    <div class="reveal">
      <div class="kicker"><?= $e($T['h_kicker']) ?></div>
      <h2><?= $e($T['h_h2']) ?></h2>
      <p class="sub"><?= $e($T['h_sub']) ?></p>
    </div>
    <div class="steps">
      <?php foreach ($T['steps'] as $i => $st): ?>
      <div class="step reveal">
        <div class="n"><?= $isEn ? ($i + 1) : ['۱', '۲', '۳'][$i] ?></div>
        <h3><?= $e($st[0]) ?></h3>
        <p><?= $e($st[1]) ?></p>
      </div>
      <?php endforeach; ?>
    </div>
  </div>
</section>

<section id="download" style="padding-top:20px">
  <div class="wrap">
    <div class="dlcard reveal">
      <div>
        <div class="kicker"><?= $e($T['d_kicker']) ?></div>
        <h2><?= $e($T['d_h2']) ?></h2>
        <p class="mut" style="margin-bottom:22px"><?= $e($T['d_desc']) ?></p>
        <a class="btn btn-p btn-lg" href="<?= $e($dlUrl) ?>">
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>
          <?= $e($dlBtnLabel) ?>
        </a>
      </div>
      <div class="spec">
        <div class="r"><b><?= $e($T['d_version']) ?></b><span><?= $ver['latest'] !== '' ? $e($ver['latest']) : '—' ?></span></div>
        <?php if ($mb !== ''): ?><div class="r"><b><?= $e($T['d_size']) ?></b><span><?= $e($mb) ?> MB</span></div><?php endif; ?>
        <div class="r"><b><?= $e($T['d_os']) ?></b><span>Windows 10/11 x64</span></div>
        <div class="r"><b><?= $e($T['d_format']) ?></b><span><?= $e($T['d_format_v']) ?></span></div>
        <?php if ($shaShort !== ''): ?><div class="r"><b><?= $e($T['d_sha']) ?></b><span class="mono" title="<?= $e($ver['sha256']) ?>"><?= $e($shaShort) ?></span></div><?php endif; ?>
      </div>
    </div>
  </div>
</section>

<section id="faq" style="padding-top:10px">
  <div class="wrap">
    <div class="reveal">
      <div class="kicker"><?= $e($T['q_kicker']) ?></div>
      <h2><?= $e($T['q_h2']) ?></h2>
    </div>
    <div class="faq reveal">
      <?php foreach ($T['faq'] as $i => $qa): ?>
      <details<?= $i === 0 ? ' open' : '' ?>>
        <summary><?= $e($qa[0]) ?></summary>
        <p><?= $e($qa[1]) ?></p>
      </details>
      <?php endforeach; ?>
    </div>
  </div>
</section>

<footer>
  <div class="wrap inner">
    <div><?= $e($T['foot_copy']) ?></div>
    <div class="fl">
      <a href="#features"><?= $e($T['foot_features']) ?></a>
      <a href="#how"><?= $e($T['foot_how']) ?></a>
      <a href="#faq"><?= $e($T['foot_faq']) ?></a>
      <a href="<?= $e($dlUrl) ?>"><?= $e($T['foot_download']) ?></a>
    </div>
  </div>
</footer>

<script>
(function () {
  var els = document.querySelectorAll('.reveal');
  if (!('IntersectionObserver' in window)) {
    els.forEach(function (el) { el.classList.add('on'); });
    return;
  }
  var io = new IntersectionObserver(function (ents) {
    ents.forEach(function (en) {
      if (en.isIntersecting) { en.target.classList.add('on'); io.unobserve(en.target); }
    });
  }, { threshold: 0.12 });
  els.forEach(function (el) { io.observe(el); });
})();
</script>
</body>
</html>
