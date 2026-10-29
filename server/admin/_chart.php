<?php
// Dashboard chart helpers (admin only): the daily series and dependency-free
// SVG rendering — no JS chart library, the graph is plain markup the panel
// already serves.

/**
 * Last $days rows, oldest first:
 *   [['d' => '2026-10-29', 'dl' => 5, 'new' => 2, 'active' => 12], ...]
 * Downloads come from dlstats.json (api/dl.php), the rest from daily.json
 * (written by stats_touch). Days without data are zero rows, so the axis
 * stays continuous.
 */
function daily_series(int $days = 30): array
{
    $daily = jread(DATA_DIR . '/daily.json', ['days' => []]);
    $dl = jread(DATA_DIR . '/dlstats.json', ['days' => []]);
    $dDays = is_array($daily['days'] ?? null) ? $daily['days'] : [];
    $lDays = is_array($dl['days'] ?? null) ? $dl['days'] : [];

    $out = [];
    for ($i = $days - 1; $i >= 0; $i--) {
        $k = date('Y-m-d', time() - $i * 86400);
        $slot = is_array($dDays[$k] ?? null) ? $dDays[$k] : [];
        $active = is_array($slot['active'] ?? null) ? $slot['active'] : [];
        $out[] = [
            'd' => $k,
            'dl' => (int)(is_array($lDays[$k] ?? null) ? ($lDays[$k]['total'] ?? 0) : 0),
            'new' => (int)($slot['new'] ?? 0),
            'active' => count($active),
        ];
    }
    return $out;
}

/**
 * Conversion downloads -> installs over the days where downloads were actually
 * counted (before the counter existed the ratio would be meaningless).
 * Returns a percentage or null when there is nothing to compare yet.
 */
function daily_conversion(array $rows): ?float
{
    $dl = 0;
    $nw = 0;
    foreach ($rows as $r) {
        if ((int)$r['dl'] > 0) {
            $dl += (int)$r['dl'];
            $nw += (int)$r['new'];
        }
    }
    return $dl > 0 ? ($nw / $dl) * 100 : null;
}

/** Largest value in a series, at least 1 so an empty chart still divides. */
function series_max(array $rows, string $key): int
{
    $max = 0;
    foreach ($rows as $r) {
        $max = max($max, (int)($r[$key] ?? 0));
    }
    return max(1, $max);
}

/**
 * Grouped bar chart (up to two series share one scale) as a plain SVG string.
 * The time axis runs left -> right (oldest first) and is marked dir="ltr"
 * inside the RTL page.
 */
function svg_bars(array $rows, array $series, int $w = 470, int $h = 180): string
{
    $padL = 30;
    $padR = 6;
    $padT = 8;
    $padB = 22;
    $plotW = $w - $padL - $padR;
    $plotH = $h - $padT - $padB;

    $max = 1;
    foreach ($series as $s) {
        $max = max($max, series_max($rows, $s['k']));
    }
    $n = max(1, count($rows));
    $slot = $plotW / $n;
    $gap = 1.5;
    $bw = max(1.5, ($slot - 3) / max(1, count($series)));

    $s = '<svg viewBox="0 0 ' . $w . ' ' . $h . '" width="100%" style="max-width:' . $w . 'px;height:auto" dir="ltr" role="img">';
    // gridlines: 0 / 50% / 100% of the scale
    foreach ([0, 0.5, 1] as $f) {
        $y = $padT + $plotH - ($plotH * $f);
        $s .= '<line x1="' . $padL . '" y1="' . $y . '" x2="' . ($w - $padR) . '" y2="' . $y . '" stroke="#1f2937" stroke-width="1"/>';
        $s .= '<text x="' . ($padL - 4) . '" y="' . ($y + 3) . '" fill="#6b7280" font-size="8" text-anchor="end">' . (int)round($max * $f) . '</text>';
    }
    foreach ($rows as $i => $r) {
        $x = $padL + $i * $slot;
        foreach ($series as $si => $sr) {
            $v = (int)($r[$sr['k']] ?? 0);
            $bh = $plotH * ($v / $max);
            $bx = $x + 1 + $si * $bw;
            $by = $padT + $plotH - $bh;
            $label = $r['d'] . ' · ' . $sr['label'] . ': ' . $v;
            $s .= '<rect x="' . round($bx, 1) . '" y="' . round($by, 1) . '" width="' . round(max(1, $bw - $gap), 1)
                . '" height="' . round(max(0, $bh), 1) . '" fill="' . $sr['c'] . '" rx="1"><title>' . h($label) . '</title></rect>';
        }
        // date labels: first, middle, last only
        if ($i === 0 || $i === $n - 1 || $i === (int)($n / 2)) {
            $tx = $x + $slot / 2;
            $anchor = $i === 0 ? 'start' : ($i === $n - 1 ? 'end' : 'middle');
            $s .= '<text x="' . round($tx, 1) . '" y="' . ($h - 6) . '" fill="#6b7280" font-size="8" text-anchor="' . $anchor . '">' . h(substr($r['d'], 5)) . '</text>';
        }
    }
    $s .= '</svg>';
    return $s;
}

/** Single-series line with a soft area fill — for the daily active users. */
function svg_line(array $rows, string $key, string $color, int $w = 470, int $h = 180): string
{
    $padL = 30;
    $padR = 6;
    $padT = 8;
    $padB = 22;
    $plotW = $w - $padL - $padR;
    $plotH = $h - $padT - $padB;

    $max = series_max($rows, $key);
    $n = max(1, count($rows));
    $step = $n > 1 ? $plotW / ($n - 1) : 0;

    $pts = [];
    foreach ($rows as $i => $r) {
        $x = $padL + $i * $step;
        $y = $padT + $plotH - ($plotH * ((int)($r[$key] ?? 0) / $max));
        $pts[] = round($x, 1) . ',' . round($y, 1);
    }

    $s = '<svg viewBox="0 0 ' . $w . ' ' . $h . '" width="100%" style="max-width:' . $w . 'px;height:auto" dir="ltr" role="img">';
    foreach ([0, 0.5, 1] as $f) {
        $y = $padT + $plotH - ($plotH * $f);
        $s .= '<line x1="' . $padL . '" y1="' . $y . '" x2="' . ($w - $padR) . '" y2="' . $y . '" stroke="#1f2937" stroke-width="1"/>';
        $s .= '<text x="' . ($padL - 4) . '" y="' . ($y + 3) . '" fill="#6b7280" font-size="8" text-anchor="end">' . (int)round($max * $f) . '</text>';
    }
    if ($pts) {
        $area = $padL . ',' . ($padT + $plotH) . ' ' . implode(' ', $pts) . ' ' . ($padL + ($n - 1) * $step) . ',' . ($padT + $plotH);
        $s .= '<polygon points="' . $area . '" fill="' . $color . '" opacity="0.15"/>';
        $s .= '<polyline points="' . implode(' ', $pts) . '" fill="none" stroke="' . $color . '" stroke-width="2" stroke-linejoin="round"/>';
        foreach ($rows as $i => $r) {
            $x = $padL + $i * $step;
            $y = $padT + $plotH - ($plotH * ((int)($r[$key] ?? 0) / $max));
            $s .= '<circle cx="' . round($x, 1) . '" cy="' . round($y, 1) . '" r="2.5" fill="' . $color . '"><title>' . h($r['d'] . ' · ' . $r[$key]) . '</title></circle>';
        }
    }
    foreach ([0, $n - 1] as $i) {
        if (!isset($rows[$i])) {
            continue;
        }
        $x = $padL + $i * $step;
        $anchor = $i === 0 ? 'start' : 'end';
        $s .= '<text x="' . round($x, 1) . '" y="' . ($h - 6) . '" fill="#6b7280" font-size="8" text-anchor="' . $anchor . '">' . h(substr($rows[$i]['d'], 5)) . '</text>';
    }
    $s .= '</svg>';
    return $s;
}
