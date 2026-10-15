<?php
if (basename($_SERVER['SCRIPT_FILENAME'] ?? '') === basename(__FILE__)) {
    http_response_code(404);
    exit;
}

define('BASE_URL', 'https://up.roxacore.com');
define('DL_DIR', __DIR__ . '/dl');
define('TZ', 'Asia/Tehran');
define('SESSION_LIFETIME', 28800);
define('APP_NAME', 'Roxa');

/**
 * B1 — داده‌ها (auth.json، stats.json، reports.json و...) باید بیرونِ webroot
 * باشند. تا وقتی داخلِ سند-ریشه بود، حفاظت فقط `.htaccess` بود که روی nginx و
 * هر وب‌سرورِ بدونِ AllowOverride بی‌اثر است.
 *
 * ترتیبِ انتخاب:
 *   1) متغیرِ محیطی ROXA_DATA — صریح‌ترین و توصیه‌شده.
 *   2) roxa-data، هم‌سطحِ پوشهٔ وب (بیرون از server/).
 *   3) اگر roxa-data هنوز ساخته نشده ولی data/ قدیمی پُر است، همان قدیمی تا
 *      زمانِ مهاجرت (tools/migrate-data.php) با هشدار ادامه می‌دهد.
 */
function roxa_data_dir(): string
{
    $env = getenv('ROXA_DATA');
    if (is_string($env) && trim($env) !== '') {
        return rtrim(trim($env), "/\\");
    }

    $new = dirname(__DIR__) . '/roxa-data';
    $old = __DIR__ . '/data';

    // اگر قدیمی داده دارد و جدید هنوز خالی است، مهاجرت نکرده‌ایم: قدیمی را با
    // هشدار ادامه بده تا پنل از کار نیفتد.
    if (is_dir($old) && !is_dir($new)) {
        return $old;
    }
    return $new;
}

define('DATA_DIR', roxa_data_dir());
