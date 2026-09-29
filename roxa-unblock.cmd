@echo off
setlocal
echo ============================================
echo  Roxa emergency unblock
echo ============================================
echo.
echo [1/3] Stopping XrayClient (clears dynamic WFP kill-switch filters)...
taskkill /F /IM XrayClient.exe >nul 2>&1
if %errorlevel%==0 (echo        XrayClient stopped.) else (echo        XrayClient was not running.)

echo [2/3] Removing legacy firewall rules (if any)...
set "RULES=%APPDATA%\Roxa\killsw-rules.txt"
if exist "%RULES%" (
    for /f "usebackq delims=" %%n in ("%RULES%") do (
        netsh advfirewall firewall delete rule name="%%n" >nul 2>&1
    )
    del /f "%RULES%" >nul 2>&1
    echo        Legacy rules removed.
) else (
    echo        No legacy rules found.
)

echo [3/3] Clearing emergency flag...
if exist "%APPDATA%\Roxa\ks-emergency.flag" del /f "%APPDATA%\Roxa\ks-emergency.flag" >nul 2>&1

echo.
echo Done. Kill-switch filters live in XrayClient's WFP session and are
echo removed automatically the moment the process closes, so your internet
echo should be fully restored now. Start XrayClient again when ready.
echo.
pause
