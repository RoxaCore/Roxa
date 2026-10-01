@echo off
setlocal
echo ============================================
echo  Roxa emergency unblock
echo ============================================
echo.
echo [1/3] Stopping Roxa (clears dynamic WFP kill-switch filters)...
taskkill /F /IM Roxa.exe >nul 2>&1
if %errorlevel%==0 (echo        Roxa stopped.) else (echo        Roxa was not running.)

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
echo Done. Kill-switch filters live in Roxa's WFP session and are
echo removed automatically the moment the process closes, so your internet
echo should be fully restored now. Start Roxa again when ready.
echo.
pause
