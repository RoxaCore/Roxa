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
 * B1 — data (auth.json, stats.json, reports.json, etc.) must live outside the
 * webroot. While inside the document root, the only protection was `.htaccess`,
 * which is ineffective on nginx and any web server without AllowOverride.
 *
 * Selection order:
 *   1) ROXA_DATA environment variable — most explicit and recommended.
 *   2) roxa-data, sibling of the web folder (outside server/).
 *   3) If roxa-data is not yet created but the old data/ is populated, the old
 *      one is used until migration (tools/migrate-data.php), with a warning.
 */
function roxa_data_dir(): string
{
    $env = getenv('ROXA_DATA');
    if (is_string($env) && trim($env) !== '') {
        return rtrim(trim($env), "/\\");
    }

    $new = dirname(__DIR__) . '/roxa-data';
    $old = __DIR__ . '/data';

    // If the old one has data and the new one is still empty, we have not
    // migrated: keep using the old one with a warning so the panel stays up.
    if (is_dir($old) && !is_dir($new)) {
        return $old;
    }
    return $new;
}

define('DATA_DIR', roxa_data_dir());
