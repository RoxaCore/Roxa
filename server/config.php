<?php
if (basename($_SERVER['SCRIPT_FILENAME'] ?? '') === basename(__FILE__)) {
    http_response_code(404);
    exit;
}

define('BASE_URL', 'https://up.roxacore.com');
define('DATA_DIR', __DIR__ . '/data');
define('DL_DIR', __DIR__ . '/dl');
define('TZ', 'Asia/Tehran');
define('SESSION_LIFETIME', 28800);
define('APP_NAME', 'Roxa');
