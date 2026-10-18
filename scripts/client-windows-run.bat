@echo off
setlocal
chcp 65001 >nul
title PersonalHub - run client on Windows
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "PROJ=%PH_ROOT%\PersonalHub.Client\PersonalHub.Client.csproj"

echo ============================================================
echo  Build + run the desktop client (net8.0-windows)
echo  handy for UI work: no phone, no adb, same gRPC code path
echo ============================================================
echo.

dotnet build "%PROJ%" -f net8.0-windows10.0.19041.0 -c Debug -t:Run
if errorlevel 1 goto :fail

exit /b 0

:fail
echo.
echo [!] Run FAILED. Scroll up for the first error.
echo.
pause
exit /b 1

:abort
echo.
pause
exit /b 1
