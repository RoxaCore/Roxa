<?php
/**
 * Download counter that redirects to the real file.
 *
 *   GET /api/dl.php?f=Roxa-Setup-3.0.9-online.exe
 *
 * Design notes:
 *  - The name must be a plain file name (no slash, no dot-path) of a file that
 *    really exists in DL_DIR; anything else is a plain 404. This endpoint can
 *    therefore never become an open redirect or a probe for server paths.
 *  - The script never reads the payload: it counts, answers 302 and lets the
 *    web server stream the bytes from dl/, so a download costs one tiny write.
 *  - Counting must never break a download: every failure here falls through to
 *    the redirect with the counter unchanged.
 *  - What is stored: totals per file, per day, and a coarse OS class taken from
 *    the User-Agent (Windows 10/11 vs older Windows vs everything else). No IP
 *    and no full User-Agent are written, so nothing identifies a person — the
 *    same rule PRIVACY.md states for the panel, and days older than 90 are
 *    dropped to match the ip/report label expiry there.
 */
require_once __DIR__ . '/../lib.php';

$f = trim((string)($_GET['f'] ?? ''));
if ($f === '' || !preg_match('/^[A-Za-z0-9][A-Za-z0-9._-]*$/', $f)) {
    http_response_code(404);
    exit;
}
if (!is_file(DL_DIR . '/' . $f)) {
    http_response_code(404);
    exit;
}

dl_hit($f);

header('Cache-Control: no-store');
header('Location: ' . BASE_URL . '/dl/' . rawurlencode($f), true, 302);
exit;

/**
 * One hit, under LOCK_EX: two downloads in the same second must both be
 * counted, and jread-then-jwrite would drop one of them (read, read, write,
 * write with the second write winning). The whole update is one lock section.
 */
function dl_hit(string $file): void
{
    $dir = DATA_DIR;
    if (!is_dir($dir)) {
        // First hit after a fresh install or a data-dir move: jwrite creates
        // the directory too, and without it fopen('c+') fails silently below.
        @mkdir($dir, 0700, true);
    }
    $path = $dir . '/dlstats.json';
    $fp = @fopen($path, 'c+');
    if ($fp === false) {
        return;
    }
    try {
        if (!flock($fp, LOCK_EX)) {
            return;
        }
        $raw = stream_get_contents($fp);
        $d = (is_string($raw) && $raw !== '') ? json_decode($raw, true) : null;
        if (!is_array($d)) {
            $d = [];
        }

        $total = (int)($d['total'] ?? 0);
        $byFile = is_array($d['byFile'] ?? null) ? $d['byFile'] : [];
        $days = is_array($d['days'] ?? null) ? $d['days'] : [];

        $day = date('Y-m-d');
        $slot = is_array($days[$day] ?? null) ? $days[$day] : [];
        $slotByFile = is_array($slot['byFile'] ?? null) ? $slot['byFile'] : [];
        $ua = is_array($slot['ua'] ?? null) ? $slot['ua'] : [];

        $total++;
        $byFile[$file] = (int)($byFile[$file] ?? 0) + 1;
        $slot['total'] = (int)($slot['total'] ?? 0) + 1;
        $slotByFile[$file] = (int)($slotByFile[$file] ?? 0) + 1;
        $cls = dl_os_class();
        $ua[$cls] = (int)($ua[$cls] ?? 0) + 1;
        $slot['byFile'] = $slotByFile;
        $slot['ua'] = $ua;
        $days[$day] = $slot;

        // Keep the last 90 days only; day strings compare correctly as
        // YYYY-MM-DD, and a long-lived counter must not grow forever.
        $cut = date('Y-m-d', time() - 90 * 86400);
        foreach (array_keys($days) as $old) {
            if ((string)$old < $cut) {
                unset($days[$old]);
            }
        }

        $json = json_encode(
            ['total' => $total, 'byFile' => $byFile, 'days' => $days],
            JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES
        );
        // false only on malformed UTF-8, which cannot occur here (all keys are
        // the ASCII file name above); still, a lost count beats a lost download.
        if ($json !== false) {
            ftruncate($fp, 0);
            rewind($fp);
            fwrite($fp, $json);
            fflush($fp);
        }
        flock($fp, LOCK_UN);
    } finally {
        fclose($fp);
    }
    @chmod($path, 0600);
}

/**
 * Windows 10, 11 and Server 2016+ all report "Windows NT 10.0" in the
 * User-Agent; Windows 7/8/8.1 report NT 6.x. That split is exactly the line
 * the site and the installer promise, so it is the only distinction kept.
 */
function dl_os_class(): string
{
    $ua = (string)($_SERVER['HTTP_USER_AGENT'] ?? '');
    if ($ua === '') {
        return 'none';
    }
    if (preg_match('/Windows NT (\d+)/', $ua, $m)) {
        return ((int)$m[1] >= 10) ? 'win10' : 'winold';
    }
    if (stripos($ua, 'Windows') !== false) {
        return 'winold';
    }
    return 'other';
}
