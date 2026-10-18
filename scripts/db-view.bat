@echo off
chcp 65001 >nul
title PersonalHub - view db

REM  Read-only viewer for the server sqlite db. No dotnet needed, python only.
for %%I in ("%~dp0..") do set "PH_ROOT=%%~fI"

where python >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] "python" was not found in PATH.
    echo     Install Python 3 and make sure it is on PATH.
    echo.
    pause
    exit /b 1
)

python "%PH_ROOT%\.tools\view_db.py"
echo.
pause
