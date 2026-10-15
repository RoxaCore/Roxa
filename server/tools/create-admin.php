<?php
/**
 * ساختِ یا تغییرِ رمزِ مدیر — فقط از راهِ خطِ فرمان روی خودِ سرور.
 *
 *   php tools/create-admin.php            رمز را می‌پرسد
 *   php tools/create-admin.php 'pass…'    رمز را از آرگومان می‌گیرد
 *
 * چرا CLI و نه صفحهٔ وب؟ ساختِ حساب از راهِ HTTP یعنی هر درخواستِ ناخواسته
 * می‌تواند رمز بسازد، و کلیدِ راه‌اندازیِ وب مجبور بود در access-log هم بماند.
 * اینجا نه توکنی هست نه لاگی.
 */

// زیرِ وب‌سرور اجرا نشود — نه فایل‌ها را بخواند نه رمز بنویسد.
if (PHP_SAPI !== 'cli') {
    fwrite(STDERR, "This script must be run from the command line.\n");
    exit(1);
}

require_once __DIR__ . '/../lib.php';

if (!is_dir(DATA_DIR) && !@mkdir(DATA_DIR, 0700, true)) {
    fwrite(STDERR, 'Cannot create DATA_DIR: ' . DATA_DIR . "\n");
    exit(1);
}

$pw = isset($argv[1]) ? (string)$argv[1] : '';
if ($pw === '') {
    echo 'New admin password (min 8 characters): ';
    // stty -echo تا رمز روی صفحه تایپ‌شده تکرار نشود (در صورتِ در دسترس بودن stty).
    if (DIRECTORY_SEPARATOR === '/') {
        @shell_exec('stty -echo 2>/dev/null');
    }
    $pw = trim((string)fgets(STDIN));
    if (DIRECTORY_SEPARATOR === '/') {
        @shell_exec('stty echo 2>/dev/null');
        echo "\n";
    }
}

if (strlen($pw) < 8) {
    fwrite(STDERR, "Password must be at least 8 characters.\n");
    exit(1);
}

$hash = password_hash($pw, PASSWORD_DEFAULT);
if ($hash === false) {
    fwrite(STDERR, "password_hash failed.\n");
    exit(1);
}

$existed = is_file(auth_file());
// کنارِ هم می‌نویسیم و rename می‌کنیم تا در صورتِ قطعِ برقِ همزمان، رمزِ قبلی
// از بین نرود و رمزِ جدید هم ناقص نوشته نشود.
$tmp = DATA_DIR . '/.auth.' . bin2hex(random_bytes(6)) . '.tmp';
if (file_put_contents($tmp, json_encode(['hash' => $hash, 'created' => date('c')], JSON_UNESCAPED_SLASHES), LOCK_EX) === false) {
    fwrite(STDERR, 'Cannot write ' . $tmp . "\n");
    exit(1);
}
@chmod($tmp, 0600);
if (!rename($tmp, auth_file())) {
    @unlink($tmp);
    fwrite(STDERR, 'Cannot move ' . $tmp . ' into place.' . "\n");
    exit(1);
}
@chmod(auth_file(), 0600);

echo ($existed ? 'Admin password changed.' : 'Admin account created.') . "\n";
echo 'File: ' . auth_file() . "\n";
echo 'Login at: ' . BASE_URL . '/admin/login.php' . "\n";
