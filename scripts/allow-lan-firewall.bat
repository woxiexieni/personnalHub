@echo off
setlocal
chcp 65001 >nul
title PersonalHub - allow phone access through Windows Firewall

REM ---------------------------------------------------------------------------
REM  Lets the phone reach the server over the LAN.
REM
REM  Right-click this file -> "Run as administrator".
REM
REM  When you need it: the phone shows a blank page / "cannot connect" for
REM  http://<pc-ip>:5001/app, or the app cannot sync, but the server window is
REM  open and both devices are on the same Wi-Fi.
REM
REM  Why this is a separate script and not part of server-run.bat: netsh needs
REM  elevation, and server-run.bat is meant to be double-clicked as a normal
REM  user. Elevating from inside it would surprise people every single launch.
REM
REM  Why 5000 AND 5001, and why profile=private,public:
REM    5000  gRPC          - the phone app talks here
REM    5001  HTTP/admin    - the browser talkes here (/app download page)
REM  Windows treats an unknown Wi-Fi as "public" by default and blocks inbound.
REM  This machine currently sits on a public-classified network, which is the
REM  exact case that silently breaks phone access.
REM
REM  ASCII only (see the note at the top of _common.bat).
REM ---------------------------------------------------------------------------

REM netsh requires elevation. "net session" fails for non-admins, so it is a
REM cheap reliable check that also works on Home editions.
net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] This needs administrator rights.
    echo     Close this window, then right-click the file and choose
    echo     "Run as administrator".
    echo.
    pause
    exit /b 1
)

echo ============================================================
echo  Allow inbound TCP 5000-5001 (LAN only, all profiles)
echo ============================================================
echo.

REM Remove any previous copy first: re-running must not pile up duplicates.
netsh advfirewall firewall delete rule name="PersonalHub Server (LAN)" >nul 2>&1

netsh advfirewall firewall add rule ^
 name="PersonalHub Server (LAN)" ^
 dir=in action=allow protocol=TCP localport=5000-5001 ^
 profile=private,public ^
 description="PersonalHub: phone app (5000) and admin/download page (5001)"
if errorlevel 1 goto :fail

echo.
echo [OK] rule added:
netsh advfirewall firewall show rule name="PersonalHub Server (LAN)" | findstr /C:"规则名称" /C:"Rule Name" /C:"已启用" /C:"Enabled" /C:"操作" /C:"Action" /C:"本地端口" /C:"LocalPort"
echo.
echo     Now open http://^<this-pc-ip^>:5001/app on the phone.
echo     To remove it later:
echo       netsh advfirewall firewall delete rule name="PersonalHub Server (LAN)"
echo.
pause
exit /b 0

:fail
echo.
echo [!] Failed to add the rule.
echo     Try running as administrator again, and check that Windows Firewall
echo     service is not disabled by a third-party security suite.
echo.
pause
exit /b 1
