@echo off
setlocal
chcp 65001 >nul
title PersonalHub Server
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

REM  NOTE: this file is deliberately 100% ASCII (no Chinese/CJK text).
REM  cmd.exe reads .bat files using the console code page (GBK by default on
REM  Chinese Windows). CJK text saved as UTF-8 gets mangled into garbage bytes,
REM  some of which are shell metacharacters - that shreds the commands and
REM  produces errors like "'rver' is not recognized". ASCII is code page proof.
REM  All Chinese output is printed by the server program itself.

set "EXE=%PH_ROOT%\PersonalHub.Server\bin\Debug\net8.0\PersonalHub.Server.exe"
set "PORT=5000"
if not "%~1"=="" set "PORT=%~1"
if defined PERSONALHUB_PORT set "PORT=%PERSONALHUB_PORT%"

REM ---- Admin UI port = gRPC port + 1 (browsers speak HTTP/1.1, gRPC needs HTTP/2) ----
set /a WEBPORT=PORT+1
if defined PERSONALHUB_WEB_PORT set "WEBPORT=%PERSONALHUB_WEB_PORT%"

echo ============================================================
echo  PersonalHub Server - starting...
echo  gRPC  (phone) : %PORT%
echo  Admin (browser): %WEBPORT%
echo ============================================================
echo.

REM ---- Pre-check: is the port already taken? ----
netstat -ano | findstr /C:":%PORT% " | findstr /C:"LISTENING" >nul
if errorlevel 1 goto start

echo [!] Port %PORT% is already in use.
echo.
echo     A server instance is probably ALREADY RUNNING.
echo     Just keep using that one - no need to start another.
echo.
echo     Listening PID:
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /C:":%PORT% " ^| findstr /C:"LISTENING"') do echo       %%p
echo.
set ANSWER=N
set /p ANSWER="    Kill it and restart anyway? [y/N] "
if /i "%ANSWER%"=="y" goto kill

echo.
echo     Cancelled - keep the existing server window open.
echo.
pause
exit /b 0

:kill
echo.
echo     Killing old instance...
for /f "tokens=5" %%p in ('netstat -ano ^| findstr /C:":%PORT% " ^| findstr /C:"LISTENING"') do taskkill /F /PID %%p >nul 2>&1
ping -n 3 127.0.0.1 >nul

:start
REM  Always build before run. The previous version only built when the exe was
REM  missing, so after a code change you could keep launching a stale binary and
REM  wonder why the UI did not change. Port is free at this point, so the build
REM  cannot be blocked by a running instance.
echo [*] Building latest code...
dotnet build "%PH_ROOT%\PersonalHub.Server\PersonalHub.Server.csproj" -c Debug --nologo -v q
if exist "%EXE%" goto run

echo [!] Build failed. Aborting.
echo.
pause
exit /b 1

:run
echo ============================================================
echo  PersonalHub Server
echo ------------------------------------------------------------
echo  Keep this window OPEN. Closing it will stop the server.
echo  The phone must be on the SAME Wi-Fi / LAN as this PC.
echo  The client app scans the LAN automatically to find it.
echo ============================================================
echo.

"%EXE%" --port %PORT%

echo.
echo Server stopped.
pause
exit /b 0

:abort
echo.
pause
exit /b 1
