<?php
/**
 * Create or change the admin password — CLI only, on the server itself.
 *
 *   php tools/create-admin.php            prompts for the password
 *   php tools/create-admin.php 'pass…'    takes the password from the argument
 *
 * Why CLI and not a web page? Creating the account over HTTP means any
 * unwanted request could set a password, and the web bootstrap key would also
 * remain in the access log. Here there is neither token nor log.
 */

// Do not run under a web server — it must neither read files nor write a password.
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
    // stty -echo so the typed password is not echoed back (when stty is available).
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
// We write beside it and rename so that in case of a simultaneous power loss,
// the previous password is not lost and the new one is not written partially.
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
