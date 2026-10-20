@echo off
REM  Launcher only. The real work is in create-keystore.ps1 (ASCII-safe bat in,
REM  proper error handling and hidden password input in PowerShell).
setlocal
chcp 65001 >nul
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0create-keystore.ps1" %*
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%
