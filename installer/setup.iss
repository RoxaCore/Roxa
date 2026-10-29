#define MyAppName "Roxa"
#define MyAppVersion "3.0.10"
#define MyAppExeName "Roxa.exe"
; ---------------------------------------------------------------------------
; دو بیلد از همین یک فایل ساخته می‌شود:
;
;   ISCC setup.iss           -> Roxa-Setup-3.0.10.exe         رانتایم داخلِ خودِ فایل
;   ISCC /DOnline setup.iss  -> Roxa-Setup-3.0.10-online.exe  رانتایم را هنگامِ نصب
;                                                         ازِ مایکروسافت می‌گیرد
;
; در بیلدِ باندل‌شده، فایلِ رانتایم در installer\runtime نشسته (در .gitignore) و
; build-installer.ps1 آن را اگر نبود از DotNetUrl دانلود و با همان SHA-256ِ زیر
; می‌سنجد؛ ناهمخوانی یعنی شکستِ build، نه تحویلِ فایلِ خراب.
;
; DotNetUrl و DotNetSha256 در هر دو بیلد معنی دارند: در بیلدِ آنلاین همان هش را
; خودِ Inno پیش از تحویلِ فایلِ دانلودشده می‌سنجد. اگر مایکروسافت لینک را عوض
; کند، build با پیامِ صریح می‌شکند و فقط همین سه خط عوض می‌شود؛ و شکستِ نصبِ
; کاربر به صفحهٔ کانالیِ https://dotnet.microsoft.com/download/dotnet/8.0 می‌رود
; که فایلی نیست و عوض نمی‌شود.
; ---------------------------------------------------------------------------
#define DotNetFile "windowsdesktop-runtime-8.0.31-win-x64.exe"
#define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe"
#define DotNetSha256 "c375dfd80a967405cfeff634912c1fccc56261ce3d9209ea473f60a21184d1cc"

[Setup]
AppId={{8F2C6A19-4B7E-4D0B-9C3A-5E1D2F8B7A64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
; نصب در Program Files، نه پوشهٔ کاربر. نصاب با PrivilegesRequired=admin اجرا
; می‌شود تا هم UAC بگیرد و هم بتواند DACLِ %ProgramData%\Roxa را از همان لحظهٔ
; نصب ببندد (ببین [Dirs] و RestrictProgramDataDir در پایین).
;
; خواندنی بودنِ Program Files عمداً مهم نیست چون برنامه دیگر در پوشهٔ خودش
; نمی‌نویسد: موتور و wintun باندل شده‌اند و فقط خوانده می‌شوند، لاگ‌ها به
; %LOCALAPPDATA%\Roxa\logs می‌روند، و نوشتنی‌ها در %APPDATA%\Roxa و
; %ProgramData%\Roxa نشسته‌اند. تنها نوشتنیِ داخلِ برنامه، بازیابیِ موتور هنگامِ
; خرابی است که فقط با ارتقا مجاز است و در غیرِ این صورت fail-closed می‌شود.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
; Inno 6 املای x64 را منسوخ اعلام می‌کند و آن را با x64os جایگزین می‌کند؛ همان را
; بدونِ هشدار می‌نویسیم. نتیجه روی ویندوزهای x64 یکی است: {autopf} همان
; C:\Program Files است.
ArchitecturesInstallIn64BitMode=x64os
OutputDir=output
#ifdef Online
OutputBaseFilename=Roxa-Setup-{#MyAppVersion}-online
#else
OutputBaseFilename=Roxa-Setup-{#MyAppVersion}
#endif
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\roxa-icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
CloseApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional tasks:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

#ifndef Online
; رانتایمِ دات‌نت — فقط در بیلدِ باندل‌شده. dontcopy یعنی به {app} کپی نمی‌شود؛
; PrepareToInstall آن را با ExtractTemporaryFile در {tmp} می‌گشاید و بی‌صدا اجرا
; می‌کند. نتیجه: نصبِ آفلاین به شبکه وابسته نمی‌شود.
;
; دانلودِ هنگامِ نصب در این بیلد نیست چون DownloadTemporaryFileِ Inno آن‌موقع
; پشتِ همان صفحهٔ «Preparing to Install» بدونِ پیشرفت و بدونِ Cancel می‌ماند و
; از WinHTTP می‌رفت که پروکسیِ کاربر را نمی‌بیند — یعنی دقیقاً همان باگی که
; گزارش شده بود. بیلدِ آنلاین (/DOnline) همان تابع را با callback و مهلت می‌گیرد.
Source: "runtime\{#DotNetFile}"; DestDir: "{tmp}"; Flags: dontcopy
#endif

; پوشهٔ وضعیتِ تونل باید از همان لحظهٔ نصب بسته باشد.
;
; Permissions را عمداً خالی گذاشته‌ایم: Inno با Permissions فقط ACE اضافه می‌کند و
; ارث‌بریِ C:\ProgramData را که به BUILTIN\Users حقِ Write می‌دهد خاموش نمی‌کند.
; روی همین ماشین آزمایش شد — هم با system-full admins-full و هم با users-readexec،
; ACEِ ارثیِ Write باقی می‌ماند و چون هر دو Allow رویِ یک SID اتحاد می‌شوند، کاربرِ
; عادی همچنان می‌نوشت. بستنِ واقعی کارِ RestrictProgramDataDir در [Code] است.
[Dirs]
Name: "{commonappdata}\Roxa"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

; حذفِ برنامه نباید سیستمِ کاربر را ول کند. اگر کاربر قبلِ حذف کرش کرده یا اتصال
; باز مانده باشد، DNS روی 127.0.0.1 و پروکسیِ loopback و مسیرهای یتیم و قواعدِ
; فایروالِ RoxaTunExcl_* می‌مانند و بعد از رفتنِ برنامه دیگر هیچ‌کس پاکشان نمی‌کند.
; waituntilterminated لازم است تا پاکسازی پیش از حذفِ فایل‌ها تمام شود.
; waituntilterminated پیش‌فرض است ولی صریح نوشته شده. املای skipifdoesntexist
; دقیقاً همانِ نامِ flag در Inno Setup است؛ با «doesnot» ناشناخته است و کلِ کامپایل
; نصب‌کننده را با خطای «Flags includes an unknown flag» می‌اندازد. بدونِ این flag،
; اگر هنگام حذف Roxa.exe از قبل رفته باشد، ویندوز هشدار «some elements could
; not be removed» نشان می‌دهد.
[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cleanup"; RunOnceID: "RoxaCleanup"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
; لاگ‌ها و حالتِ تونل جزوِ خروجیِ برنامه‌اند و بعد از حذف بی‌ارزش‌اند.
Type: filesandordirs; Name: "{app}\logs"

[Code]
// ---------------------------------------------------------------------------
// گیتِ نسخهٔ ویندوز — باید نخستین چیزی باشد که اجرا می‌شود.
//
// برنامه به .NET Desktop Runtime 8 نیاز دارد و کفِ پشتیبانیِ رسمیِ آن ویندوز ۱۰
// نسخهٔ ۱۶۰۷ (build 14393) است. روی ویندوز ۷/۸/۸٫۱ همان نصب‌کنندهٔ رانتایم هم
// اصلاً نصب نمی‌شود، پس رفتارِ قبلی یعنی دانلودِ چند مگابایت و بعد خطای
// «نصب رانتایم کامل نشد، دوباره تلاش کنید» — پیامی کاملاً بی‌ربط و گمراه‌کننده
// بود. جلوی این می‌گیریم.
//
// نکته‌ها:
//  - ویندوز ۱۱ همچنان Major=10 و Build>=22000 را گزارش می‌دهد، پس همان شرط
//    آن را هم درست می‌پذیرد.
//  - GetWindowsVersionEx یک procedure است و همیشه رکورد را پر می‌کند؛ در
//    Pascal Scriptِ Inno تابعِ بولی نیست.
//  - Windows Server 2012 R2 (build 9600) با این گیت رد می‌شود هرچند .NET 8
//    فنیاً آن را می‌پذیرد — این برنامه یک کلاینتِ دسکتاپ است. سرورهای جدیدتر
//    (۲۰۱۶ و بالاتر، build>=14393) همچنان پذیرفته می‌شوند.
//  - کامنت در [Code] باید از نوعِ Pascal باشد: // یا { } یا ( * * ).
//    کاراکترِ نقطه‌ویرگول فقط در بخش‌های دیگرِ اسکریپت کامنت است و اینجا آن را
//    به‌عنوانِ کد پارس می‌کند و کلِ کامپایل با «'BEGIN' expected» می‌شکند.
// ---------------------------------------------------------------------------
const
  MinWindowsMajor = 10;
  MinWindowsBuild = 14393;

#ifdef Online
// مهلت‌های دانلودِ رانتایم — فقط بیلدِ آنلاین.
//
// عمداً در همان بخشِ ابتدایی آمده‌اند: Pascal Script برای هر بلاک فقط یک بخشِ
// const و یک بخشِ var می‌پذیرد و آنها باید پیش از توابع بیایند.
const
  DlTimeoutMs = 600000;   // مهلتِ سختِ کلِ دانلود: ۱۰ دقیقه
  DlStallMs = 90000;      // ۹۰ ثانیه بدونِ هیچ پیشرفتی

var
  DlStart: LongWord;      // لحظهٔ شروعِ دانلود
  DlAdvanced: LongWord;   // لحظهٔ آخرین پیشرفتِ واقعی
  DlProgress: Int64;      // بایتِ دیده‌شده تا اینجا
#endif

function IsSupportedWindows: Boolean;
var
  V: TWindowsVersion;
begin
  Result := False;
  GetWindowsVersionEx(V);

  if V.Major > MinWindowsMajor then
  begin
    Result := True;
    Exit;
  end;

  Result := (V.Major = MinWindowsMajor) and (V.Build >= MinWindowsBuild);
end;

// ---------------------------------------------------------------------------
// Windows Server Core را کنار می‌گذاریم.
//
// خودِ .NET 8 Server Core را در ماتریسِ پشتیبانی دارد، پس نصبِ رانتایم شکست
// نمی‌خورد — ولی برنامه عملاً غیرقابلِ استفاده می‌شود: نشستِ دسکتاپی وجود
// ندارد که پنجره یا tray icon در آن نمایش داده شود. بدونِ این بررسی، کاربرِ
// Server Core چند ده مگابایت دانلود می‌کند و بعد یک خطای گمراه‌کننده دربارهٔ
// رانتایم می‌گیرد، در حالی که مشکل اصلاً رانتایم نیست.
//
// explorer.exe نشانهٔ قابلِ اتکاست: Server Core آن را نصب نمی‌کند، و رویِ
// ویندوزِ دسکتاپِ سالم همیشه هست. ProductType هم از همان رکوردِ
// GetWindowsVersionEx می‌آید (VER_NT_WORKSTATION یعنی نسخهٔ دسکتاپ).
// ---------------------------------------------------------------------------
function IsServerEdition: Boolean;
var
  V: TWindowsVersion;
begin
  GetWindowsVersionEx(V);
  // صفر یعنی نتوانسته تشخیص دهد؛ در آن حالت چیزی را رد نکن.
  Result := (V.ProductType <> 0) and (V.ProductType <> VER_NT_WORKSTATION);
end;

function HasDesktopShell: Boolean;
begin
  Result := FileExists(ExpandConstant('{win}\explorer.exe'));
end;

function InitializeSetup: Boolean;
var
  V: TWindowsVersion;
  Found: String;
begin
  Result := True;

  if not IsSupportedWindows then
  begin
    Result := False;
    GetWindowsVersionEx(V);
    Found := Format('%d.%d (build %d)', [V.Major, V.Minor, V.Build]);
    SuppressibleMsgBox(
      '{#MyAppName} cannot be installed on this version of Windows.' + #13#10 + #13#10 +
      'This application requires the .NET Desktop Runtime 8, and Microsoft no ' +
      'longer supports this Windows version. That runtime will neither install ' +
      'nor run here.' + #13#10 + #13#10 +
      'Required: Windows 10 version 1607 (build ' + IntToStr(MinWindowsBuild) +
      ') or newer, including Windows 11.' + #13#10 +
      'Detected on this PC: ' + Found + #13#10 + #13#10 +
      'Nothing was installed.',
      mbCriticalError, MB_OK, IDOK);
    Exit;
  end;

  if IsServerEdition and (not HasDesktopShell) then
  begin
    Result := False;
    SuppressibleMsgBox(
      '{#MyAppName} cannot be installed on Windows Server Core.' + #13#10 + #13#10 +
      'Roxa is a desktop application with a window and a tray icon, and Server ' +
      'Core has no desktop session to show them in, so it would never be usable.' + #13#10 + #13#10 +
      'Install Windows Server with the Desktop Experience option, or use ' +
      'Windows 10/11 instead.' + #13#10 + #13#10 + 'Nothing was installed.',
      mbCriticalError, MB_OK, IDOK);
    Exit;
  end;
end;

function DirHasVersion8(const BaseDir: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(BaseDir + '\8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsDotNetDesktopInstalled: Boolean;
begin
  Result :=
    DirHasVersion8(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    DirHasVersion8(ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App')) or
    DirHasVersion8(ExpandConstant('{localappdata}\dotnet\shared\Microsoft.WindowsDesktop.App'));
end;

// ---------------------------------------------------------------------------
// نوشتنِ وضعیتِ پشتِ صفحهٔ «Preparing to Install» — مشترکِ هر دو بیلد.
//
// بدونِ این، هر دو کارِ طولانیِ این صفحه (استخراجِ فایلِ ۵۶ مگابایتی، دانلود،
// نصبِ بی‌صدای رانتایم) دقیقاً همان «هیچ اتفاقی نمی‌افتد» را نشان می‌داد که
// گزارش شده بود. Update مستقیمِ همان کنترل را رسم می‌کند، پس حتی اگر حلقهٔ
// پیامِ ویندوز وسطِ کار درگیر باشد هم متن تازه دیده می‌شود.
//
// در حالتِ /SILENT ممکن است فرمِ ویزارد در دسترس نباشد؛ چون فقط رابطِ کاربری
// است، خطا را می‌بلعیم و کارِ اصلی ادامه پیدا می‌کند.
// ---------------------------------------------------------------------------
procedure SetPreparingStatus(const S: String; const Percent: Integer);
begin
  try
    WizardForm.PreparingLabel.Caption := S;
    if (Percent >= 0) and (Percent <= 1000) then
    begin
      WizardForm.ProgressGauge.Max := 1000;
      WizardForm.ProgressGauge.Position := Percent;
    end;
    WizardForm.PreparingLabel.Update;
    WizardForm.ProgressGauge.Update;
  except
    Log('[prep] wizard UI not available (silent install?); continuing headless');
  end;
end;

// ---------------------------------------------------------------------------
// راهِ خروجِ دستی وقتی نصبِ رانتایم شکست بخورد — مشترکِ هر دو بیلد.
//
// صفحهٔ مایکروسافت در مرورگرِ کاربر باز می‌شود: مرورگر از پروکسیِ کاربر
// استفاده می‌کند و در همان شبکه‌ای هم که دسترسیِ مستقیم بسته است جواب می‌دهد.
// ---------------------------------------------------------------------------
function RuntimeManualFallback(const Why: String): String;
var
  OpenCode: Integer;
begin
  ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '',
            SW_SHOWNORMAL, ewNoWait, OpenCode);
  Result := 'The .NET Desktop Runtime 8 could not be installed automatically' +
            ' (' + Why + ').' + #13#10 + #13#10 +
            'Microsoft''s download page was opened in your browser. Install the ' +
            '".NET Desktop Runtime 8.0.x - x64" there, then run this setup again.'
            + #13#10 + #13#10 + 'Nothing was installed.';
end;

#ifdef Online
// ---------------------------------------------------------------------------
// بیلدِ آنلاین: رانتایم داخلِ فایل نیست و هنگامِ نصب ازِ مایکروسافت دانلود
// می‌شود.
//
// چرا DownloadTemporaryFileِ خودِ Inno، نه یک دانلودگرِ دست‌ساز:
//  - SHA-256ِ تعبیه‌شده را پیش از تحویلِ فایل می‌سنجد؛ اگر روزی بایتِ دیگری
//    پشتِ همان لینک بنشیند، هش می‌خورد و اجرا نمی‌شود.
//  - حینِ کار به OnDownloadProgress پیام می‌دهد؛ یعنی حلقهٔ پیامِ ویندوز
//    می‌چرخد، هم پیشرفت رسم می‌شود و هم دکمهٔ Cancel زنده می‌ماند.
//  - بازگرداندنِ False از آن تابع دانلود را متوقف می‌کند؛ همین اهرمِ مهلتِ
//    سخت و قطعِ اتصالِ بی‌پیشرفت است، پس «آویزان شدنِ تا ابد» ممکن نیست.
//
// لایهٔ سوم، مهلتِ دریافتِ خودِ WinHTTP است: اگر اصلاً پیامی نرسد، استثناء
// می‌دهد و به مسیرِ دستی می‌رویم. سه لایه با هم همان باگِ گزارش‌شده را می‌بندند.
// ---------------------------------------------------------------------------
function GetTickCount: LongWord;
  external 'GetTickCount@kernel32.dll stdcall';

function DlElapsedMs(const Since: LongWord): Int64;
var
  NowTick: LongWord;
begin
  NowTick := GetTickCount;
  if NowTick >= Since then
    Result := NowTick - Since
  else
    // GetTickCount حدودِ ۴۹٫۷ روز یک‌بار برمی‌گردد. در آن حالت مبنا را همان
    // ابتدا می‌گیریم و مهلتِ سخت دوباره شروع می‌شود؛ نه اینکه منفی شود و بی‌مهلت
    // ادامه دهد.
    Result := 0;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
var
  Percent: Integer;
begin
  Result := True;

  if DlElapsedMs(DlStart) > DlTimeoutMs then
  begin
    Log('[dl] aborted: longer than ' + IntToStr(DlTimeoutMs div 1000) + ' seconds');
    Result := False;
    Exit;
  end;

  if Progress > DlProgress then
  begin
    DlProgress := Progress;
    DlAdvanced := GetTickCount;
  end
  else if DlElapsedMs(DlAdvanced) > DlStallMs then
  begin
    Log('[dl] aborted: no progress for ' + IntToStr(DlStallMs div 1000) + ' seconds');
    Result := False;
    Exit;
  end;

  if ProgressMax > 0 then
  begin
    Percent := Integer((Progress * 1000) div ProgressMax);
    SetPreparingStatus('Downloading the .NET Desktop Runtime 8 from Microsoft: ' +
      IntToStr(Progress div 1048576) + ' of ' + IntToStr(ProgressMax div 1048576) +
      ' MB', Percent);
  end
  else
    SetPreparingStatus('Downloading the .NET Desktop Runtime 8 from Microsoft...', -1);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
  RuntimePath: String;
  DlBytes: Int64;
  Ok: Boolean;
begin
  Result := '';
  if IsDotNetDesktopInstalled then
    Exit;

  // SuppressibleMsgBox تا در حالتِ /SILENT آویزان نماند و خودش IDOK برگرداند.
  SuppressibleMsgBox(
    'The .NET Desktop Runtime 8 is not installed on this PC.' + #13#10 +
    'Setup will download it from Microsoft now (about 56 MB).',
    mbInformation, MB_OK, IDOK);

  RuntimePath := ExpandConstant('{tmp}\{#DotNetFile}');

  DlStart := GetTickCount;
  DlAdvanced := DlStart;
  DlProgress := 0;
  SetPreparingStatus('Downloading the .NET Desktop Runtime 8 from Microsoft...', -1);

  try
    DlBytes := DownloadTemporaryFile('{#DotNetUrl}', '{#DotNetFile}', '{#DotNetSha256}',
      @OnDownloadProgress);
    Log('[dl] downloaded ' + IntToStr(DlBytes) + ' bytes');
  except
    // شاملِ نرسیدن به شبکه، هشِ نامعتبر و لغوِ خودِ کاربر می‌شود.
    Result := RuntimeManualFallback(GetExceptionMessage);
    Exit;
  end;

  if not FileExists(RuntimePath) then
  begin
    Result := RuntimeManualFallback('the download reported success but no file appeared.');
    Exit;
  end;

  SetPreparingStatus('Installing the .NET Desktop Runtime 8. This can take a minute...', -1);

  Ok := Exec(RuntimePath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if Ok and ((ErrorCode = 0) or (ErrorCode = 3010)) then
  begin
    // 3010 یعنی «موفق، ری‌استارت لازم است»؛ باید صفحهٔ پایانیِ ری‌استارت را
    // نشان دهیم نه اینکه آن را به‌عنوانِ شکست گزارش کنیم.
    if ErrorCode = 3010 then
      NeedsRestart := True;
    Sleep(1500);
    if IsDotNetDesktopInstalled then
      Exit;
    Result := RuntimeManualFallback('the installer finished but no runtime appeared.');
    Exit;
  end;

  if Ok then
    Result := RuntimeManualFallback('the installer exited with code ' + IntToStr(ErrorCode) + '.')
  else
    Result := RuntimeManualFallback('the installer could not be started.');
end;
#else
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
  RuntimePath: String;
  Ok: Boolean;
begin
  Result := '';
  if IsDotNetDesktopInstalled then
    Exit;

  // SuppressibleMsgBox تا در حالتِ /SILENT آویزان نماند و خودش IDOK برگرداند.
  SuppressibleMsgBox(
    'The .NET Desktop Runtime 8 is not installed on this PC.' + #13#10 +
    'Setup will install it now. This step does not need an internet connection.',
    mbInformation, MB_OK, IDOK);

  // صفحهٔ «Preparing to Install» تا پایانِ همین تابع باز است؛ بدونِ این متن،
  // استخراجِ فایلِ ۵۶ مگابایتی و نصبِ بی‌صدای رانتایم دقیقاً همان «هیچ اتفاقی
  // نمی‌افتد» را نشان می‌داد که گزارش شده بود.
  SetPreparingStatus('Installing the bundled .NET Desktop Runtime 8. Please wait...', -1);

  RuntimePath := ExpandConstant('{tmp}\{#DotNetFile}');
  try
    ExtractTemporaryFile('{#DotNetFile}');
  except
    // Pascal Scriptِ Inno ساختارِ «on E: Exception do» را ندارد؛ پیامِ استثنایِ
    // جاری با GetExceptionMessage خوانده می‌شود (با یک اسکریپتِ کاوش ثابت شد).
    Result := RuntimeManualFallback('the bundled copy could not be extracted: ' + GetExceptionMessage);
    Exit;
  end;

  if not FileExists(RuntimePath) then
  begin
    Result := RuntimeManualFallback('the bundled copy is missing from this setup file.');
    Exit;
  end;

  SetPreparingStatus('Installing the bundled .NET Desktop Runtime 8. This can take a minute...', -1);

  Ok := Exec(RuntimePath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if Ok and ((ErrorCode = 0) or (ErrorCode = 3010)) then
  begin
    // 3010 یعنی «موفق، ری‌استارت لازم است»؛ باید صفحهٔ پایانیِ ری‌استارت را
    // نشان دهیم نه اینکه آن را به‌عنوانِ شکست گزارش کنیم.
    if ErrorCode = 3010 then
      NeedsRestart := True;
    Sleep(1500);
    if IsDotNetDesktopInstalled then
      Exit;
    Result := RuntimeManualFallback('the installer finished but no runtime appeared.');
    Exit;
  end;

  if Ok then
    Result := RuntimeManualFallback('the installer exited with code ' + IntToStr(ErrorCode) + '.')
  else
    Result := RuntimeManualFallback('the installer could not be started.');
end;
#endif

// ---------------------------------------------------------------------------
// بستنِ DACLِ %ProgramData%\Roxa در همان لحظهٔ نصب.
//
// اینجا باید /inheritance:r بزنیم، نه Permissions در [Dirs]. هر دو مسیر روی همین
// ماشین آزمایش شدند: پوشهٔ تازه در C:\ProgramData ارث می‌برد از
//   BUILTIN\Users  ReadAndExecute, Synchronize
//   BUILTIN\Users  Write                                  <-- اینجا
// و Inno با Permissions فقط ACEِ صریح اضافه می‌کند. چون Allow برایِ یک SID به هم
// جوش نمی‌خورد بلکه اتحاد می‌شود، اضافه کردنِ users-readexec هم چیزی را عوض نمی‌کرد.
//
// /inheritance:r همهٔ ACEهای ارثی را برمی‌دارد و /grant:r سه ACEِ صریح می‌گذارد:
// SYSTEM و Administrators کامل، BUILTIN\Users فقط خواندن+اجرا. پس بچه‌ها
// (tun-state.json) هم همین را ارث می‌برند.
//
// SIDs به‌جای نام نوشته می‌شوند چون خروجیِ icacls محلی‌سازی شده است؛
// *S-1-5-18 و *S-1-5-32-544 و *S-1-5-32-545 روی همهٔ ویندوزها همین‌ها هستند.
//
// بهترین تلاش است: اگر icacls در دسترس نباشد نصب متوقف نمی‌شود، چون خودِ برنامه
// همین ACL را در هر شروع اعمال می‌کند (EnsureProgramDataAcl) و تا آن زمان هم
// Load() هر ورودیِ tun-state.json را اعتبارسنجی می‌کند.
// ---------------------------------------------------------------------------
procedure RestrictProgramDataDir;
var
  Params: String;
  ResultCode: Integer;
begin
  Params := '"' + ExpandConstant('{commonappdata}\Roxa') + '" /inheritance:r /grant:r'
    + ' *S-1-5-18:(OI)(CI)F'
    + ' *S-1-5-32-544:(OI)(CI)F'
    + ' *S-1-5-32-545:(OI)(CI)RX';
  if Exec(ExpandConstant('{sys}\icacls.exe'), Params, '', SW_HIDE,
          ewWaitUntilTerminated, ResultCode) then
    Log('[acl] icacls exit=' + IntToStr(ResultCode))
  else
    Log('[acl] icacls could not start; Roxa will tighten this ACL on first run');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  // ssPostInstall یعنی [Dirs] و [Files] همه اجرا شده‌اند.
  if CurStep = ssPostInstall then
    RestrictProgramDataDir;
end;
