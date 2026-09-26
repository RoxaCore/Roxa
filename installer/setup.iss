#define MyAppName "Roxa"
#define MyAppVersion "1.0.0"
#define MyAppExeName "XrayClient.exe"
#define DotNetUrl "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/8.0.31/windowsdesktop-runtime-8.0.31-win-x64.exe"
#define DotNetSha256 "c375dfd80a967405cfeff634912c1fccc56261ce3d9209ea473f60a21184d1cc"

[Setup]
AppId={{8F2C6A19-4B7E-4D0B-9C3A-5E1D2F8B7A64}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={autopf}\{#MyAppName}
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
Name: "desktopicon"; Description: "میانبر روی دسکتاپ"; GroupDescription: "میانبرها:"

[Files]
Source: "publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "اجرای {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
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

  MsgBox('.NET Desktop Runtime 8 روی سیستم شما نصب نیست.' + #13#10 +
         'برای ادامه نصب، دانلود و نصب خودکار آن انجام می‌شود (نیاز به اینترنت است).',
         mbInformation, MB_OK);

  RuntimePath := ExpandConstant('{tmp}\windowsdesktop-runtime.exe');
  try
    Downloaded := DownloadTemporaryFile('{#DotNetUrl}', 'windowsdesktop-runtime.exe', '{#DotNetSha256}', nil);
  except
    ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := 'دانلود رانتایم .NET ممکن نشد. اتصال اینترنت را بررسی کنید یا از لینک بازشده دستی نصب کنید و سپس دوباره این ستاپ را اجرا کنید.';
    Exit;
  end;

  Ok := Exec(RuntimePath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, ErrorCode);
  if Ok and ((ErrorCode = 0) or (ErrorCode = 3010)) then
  begin
    Sleep(1500);
    if IsDotNetDesktopInstalled then
      Exit;
  end;

  Result := 'نصب رانتایم .NET کامل نشد. دوباره تلاش کنید و هنگام پرسش UAC گزینه «بله» را بزنید.';
end;
