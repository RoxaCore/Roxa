@echo off
setlocal
echo ============================================
echo  Roxa emergency unblock
echo ============================================
echo.
echo [1/4] Stopping Roxa (clears dynamic WFP kill-switch filters)...
taskkill /F /IM Roxa.exe >nul 2>&1
if %errorlevel%==0 (echo        Roxa stopped.) else (echo        Roxa was not running.)

echo [2/4] Removing exclusion firewall rules...
rem  RoxaTunExcl_* is the CURRENT rule set: "dir=out action=block UDP 443"
rem  pinned to one specific program. Unlike the kill-switch (which lives in
rem  WFP and dies with the process) these sit in Windows Firewall, so an
rem  unclean exit leaves them behind and that program silently loses UDP 443.
rem  To the user it just looks like "that app has a broken internet".
set "ERULES=%APPDATA%\Roxa\tunexcl-rules.txt"
if exist "%ERULES%" (
    for /f "usebackq delims=" %%n in ("%ERULES%") do (
        netsh advfirewall firewall delete rule name="%%n" >nul 2>&1
    )
    del /f "%ERULES%" >nul 2>&1
    echo        Exclusion rules removed.
) else (
    echo        No exclusion rule list found.
)

echo [3/4] Removing legacy firewall rules (if any)...
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

rem  Safety net: the rule lists above are the only source of truth, so if a
rem  crash ever happened between "delete rule" and "delete list", or the list
rem  was lost, the rules would stay for good. netsh accepts a wildcard in
rem  name=, so sweep by prefix as a last resort. Failures are swallowed on
rem  purpose: a missing prefix simply means there is nothing to remove.
netsh advfirewall firewall delete rule name="RoxaTunExcl_*" >nul 2>&1
netsh advfirewall firewall delete rule name="RoxaKillSw_*" >nul 2>&1
echo        Prefix sweep done.

echo [4/4] Clearing emergency flag...
rem  Leftover flag would disarm the kill-switch on the next start without the
rem  user ever asking for it.
if exist "%APPDATA%\Roxa\ks-emergency.flag" del /f "%APPDATA%\Roxa\ks-emergency.flag" >nul 2>&1

echo.
echo Done. Kill-switch filters live in Roxa's WFP session and are
echo removed automatically the moment the process closes. The exclusion
echo rules above were the ones that could outlive Roxa, and they are now
echo gone too, so your internet should be fully restored.
echo Start Roxa again when ready.
echo.
pause
