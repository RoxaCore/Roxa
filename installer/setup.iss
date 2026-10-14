#define MyAppName "Roxa"
#define MyAppVersion "3.0.8"
#define MyAppExeName "Roxa.exe"
#define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe"
#define DotNetSha256 "c375dfd80a967405cfeff634912c1fccc56261ce3d9209ea473f60a21184d1cc"

[Setup]
AppId={{8F2C6A19-4B7E-4D0B-9C3A-5E1D2F8B7A64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
; همیشه در پوشهٔ کاربر نصب می‌شود، نه Program Files. نصاب با PrivilegesRequired=lowest
; اجرا می‌شود، پس {autopf} فقط وقتی به Program Files می‌رفت که کاربر عمداً نصاب را
; «Run as administrator» می‌زد — و آن موقع خودِ برنامه (asInvoker) در پوشهٔ خودش حقِ
; نوشتن نداشت: لاگ گم می‌شد، دانلودِ موتور و وینتین شکست می‌خورد و آپدیت اعمال
; می‌شد ولی برنامه دیگر برنمی‌گشت. {localappdata}\Programs همیشه قابلِ نوشتن است.
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename=Roxa-Setup-{#MyAppVersion}
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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ErrorCode: Integer;
  RuntimePath: String;
  Ok: Boolean;
  Downloaded: Int64;
begin
  Result := '';
  if IsDotNetDesktopInstalled then
    Exit;

  // SuppressibleMsgBox تا در حالتِ /SILENT آویزان نماند و خودش IDOK برگرداند.
  SuppressibleMsgBox(
    'The .NET Desktop Runtime 8 is not installed on this PC.' + #13#10 +
    'Setup will download and install it automatically to continue ' +
    '(an internet connection is required).',
    mbInformation, MB_OK, IDOK);

  RuntimePath := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  try
    Downloaded := DownloadTemporaryFile('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '{#DotNetSha256}', nil);
  except
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := 'The .NET runtime could not be downloaded. Check your internet ' +
              'connection, or install it manually from the page that just opened ' +
              'and then run this setup again.';
    Exit;
  end;

  Ok := Exec(RuntimePath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if Ok and ((ErrorCode = 0) or (ErrorCode = 3010)) then
  begin
    Sleep(1500);
    if IsDotNetDesktopInstalled then
      Exit;
  end;

  Result := 'The .NET runtime was not installed successfully. Try again and ' +
            'click "Yes" when Windows asks for permission (UAC).';
end;
