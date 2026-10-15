<?php
/**
 * مهاجرتِ داده‌ها: بیرون‌بردنِ داده‌ها از webroot + پاک‌سازیِ IPهایِ خام.
 *
 *   php tools/migrate-data.php           فقط گزارش می‌دهد (dry-run)
 *   php tools/migrate-data.php --apply   واقعاً اجرا می‌کند
 *
 * دو کار انجام می‌شود:
 *   ۱) اگر داده‌ها هنوز در server/data/ باشند، به DATA_DIRِ بیرونِ وب‌ریشه
 *      منتقل می‌شوند (config.php پیش‌فرض را خارج گذاشته، ولی اگر قبلاً داخلِ
 *      webroot مستقر شده باشید این اسکریپت آن‌ها را جابه‌جا می‌کند).
 *   ۲) هر IPِ خامی که در stats.json یا reports.json مانده باشد، به هشِ نمکیِ
 *      همین سرور تبدیل می‌شود. نمک ساخته می‌شود اگر نباشد.
 *
 * نکته: بعد از پاک‌سازی، ISP/ASNِ ثبت‌شده پابرجاست ولی دیگر نمی‌شود IP را
 * برگرداند؛ همین هدف است.
 */

if (PHP_SAPI !== 'cli') {
    fwrite(STDERR, "This script must be run from the command line.\n");
    exit(1);
}

require_once __DIR__ . '/../lib.php';

$apply = in_array('--apply', array_slice($argv, 1), true);
$oldDir = __DIR__ . '/../data';
$oldDir = rtrim(str_replace('\\', '/', $oldDir), '/');
$newDir = rtrim(str_replace('\\', '/', DATA_DIR), '/');

echo ($apply ? "APPLY  " : "DRY-RUN") . "\n";
echo 'old data dir : ' . $oldDir . "\n";
echo 'data dir     : ' . $newDir . "\n\n";

$srcFiles = [];
if (is_dir($oldDir)) {
    foreach ((array)@scandir($oldDir) as $n) {
        if ($n === '.' || $n === '..' || $n === '.htaccess') {
            continue;
        }
        $p = $oldDir . '/' . $n;
        if (is_file($p)) {
            $srcFiles[$n] = $p;
        }
    }
}

// ۱) جابه‌جایی
if ($newDir === $oldDir) {
    echo "skip: DATA_DIR is still the old in-webroot directory (check config.php / ROXA_DATA).\n";
} elseif ($srcFiles) {
    if ($apply && !is_dir($newDir) && !@mkdir($newDir, 0700, true)) {
        fwrite(STDERR, "Cannot create $newDir\n");
        exit(1);
    }
    foreach ($srcFiles as $n => $p) {
        $dst = $newDir . '/' . $n;
        echo ($apply ? 'move ' : 'would move ') . $p . ' -> ' . $dst . "\n";
        if ($apply) {
            if (!@rename($p, $dst)) {
                // rename ممکن است بین دو پارتیشن شکست بخورد؛ کپی+حذف.
                if (@copy($p, $dst)) {
                    @unlink($p);
                } else {
                    fwrite(STDERR, "  FAILED\n");
                    exit(1);
                }
            }
            @chmod($dst, 0600);
        }
    }
} else {
    echo "skip: nothing to move.\n";
}

echo "\n";

// ۲) پاک‌سازیِ IPِ خام
$scrub = static function (string $label, string $file, callable $fix) use ($apply): void {
    if (!is_file($file)) {
        echo "skip: $label (not present)\n";
        return;
    }
    $raw = @file_get_contents($file);
    $j = is_string($raw) ? json_decode($raw, true) : null;
    if (!is_array($j)) {
        echo "skip: $label (unreadable)\n";
        return;
    }
    [$j, $n] = $fix($j);
    echo sprintf("%-14s %d raw IP(s) %s\n", $label, $n, $n === 0 ? 'clean' : ($apply ? 'hashed' : 'would hash'));
    if ($apply && $n > 0) {
        jwrite($file, $j);
    }
};

$looksLikeIp = static fn(string $v): bool => filter_var($v, FILTER_VALIDATE_IP) !== false;

$scrub('stats.json', $newDir . '/stats.json', static function (array $j) use ($looksLikeIp) {
    $n = 0;
    if (!is_array($j['clients'] ?? null)) {
        return [$j, 0];
    }
    foreach ($j['clients'] as $id => &$c) {
        if (!is_array($c)) {
            continue;
        }
        foreach (['ip', 'ip_last'] as $k) {
            $v = (string)($c[$k] ?? '');
            if ($v !== '' && $looksLikeIp($v)) {
                $c[$k] = ip_key($v);
                $n++;
            }
        }
    }
    unset($c);
    return [$j, $n];
});

$scrub('reports.json', $newDir . '/reports.json', static function (array $j) use ($looksLikeIp) {
    $n = 0;
    if (!is_array($j['items'] ?? null)) {
        return [$j, 0];
    }
    foreach ($j['items'] as &$it) {
        if (!is_array($it)) {
            continue;
        }
        $v = (string)($it['ip'] ?? '');
        if ($v !== '' && $looksLikeIp($v)) {
            $it['src'] = ip_key($v);
            $n++;
        }
    }
    unset($it);
    return [$j, $n];
});

echo "\n";
if ($apply) {
    echo 'salt file     : ' . DATA_DIR . "/.salt\n";
    echo "done.\n";
} else {
    echo "run again with --apply to perform these changes.\n";
}
