<?php
require_once __DIR__ . '/../lib.php';
sess_start();
$_SESSION = [];
session_destroy();
header('Location: login.php');
exit;
