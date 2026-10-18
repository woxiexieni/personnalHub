@echo off
setlocal
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

REM Opens the web admin page in the default browser.
REM The admin UI listens on (gRPC port + 1) by default: 5000 -> 5001.
REM   usage: scripts\admin-open.bat            (uses 5000 / 5001)
REM          scripts\admin-open.bat 5090       (uses 5090 / 5091)

set "PORT=5000"
if not "%~1"=="" set "PORT=%~1"
if defined PERSONALHUB_PORT set "PORT=%PERSONALHUB_PORT%"

set /a WEBPORT=PORT+1
if defined PERSONALHUB_WEB_PORT set "WEBPORT=%PERSONALHUB_WEB_PORT%"

echo.
echo  Opening admin UI: http://127.0.0.1:%WEBPORT%/
echo  (if the page does not load, the server is probably not running)
echo.

start "" "http://127.0.0.1:%WEBPORT%/"
exit /b 0

:abort
echo.
pause
exit /b 1
