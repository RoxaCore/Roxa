<?php
require_once __DIR__ . '/_layout.php';
require_auth();

$err = '';
if ($_SERVER['REQUEST_METHOD'] === 'POST') {
    csrf_check();
    $cur = (string)($_POST['current'] ?? '');
    $p1 = (string)($_POST['password'] ?? '');
    $p2 = (string)($_POST['confirm'] ?? '');
    $a = jread(auth_file(), []);
    if (empty($a['hash']) || !password_verify($cur, (string)$a['hash'])) {
        $err = 'رمز فعلی اشتباه است.';
    } elseif (strlen($p1) < 8) {
        $err = 'رمز جدید باید حداقل ۸ کاراکتر باشد.';
    } elseif ($p1 !== $p2) {
        $err = 'تکرار رمز جدید مطابقت ندارد.';
    } else {
        $a['hash'] = password_hash($p1, PASSWORD_DEFAULT);
        $a['changed'] = date('c');
        jwrite(auth_file(), $a);
        flash('رمز عبور تغییر کرد.');
        header('Location: password.php');
        exit;
    }
}

page_head('تغییر رمز', 'pw');

echo '<div class="card"><h2>تغییر رمز پنل</h2>';
if ($err !== '') {
    echo '<div class="msg err">' . h($err) . '</div>';
}
echo '<form method="post">' . csrf_field();
echo '<label>رمز فعلی</label><input type="password" name="current" required>';
echo '<label>رمز جدید (حداقل ۸ کاراکتر)</label><input type="password" name="password" required>';
echo '<label>تکرار رمز جدید</label><input type="password" name="confirm" required>';
echo '<button class="btn" type="submit">تغییر رمز</button></form></div>';

page_foot();
