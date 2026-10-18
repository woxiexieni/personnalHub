@echo off
setlocal
chcp 65001 >nul
title PersonalHub - publish server
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "PROJ=%PH_ROOT%\PersonalHub.Server\PersonalHub.Server.csproj"
set "OUTDIR=%PH_ROOT%\out\server"

echo ============================================================
echo  Publish server to: %OUTDIR%
echo  version: %PH_VERSION%
echo.
echo  no argument         -> needs .NET 8 runtime on the target PC (small)
echo  server-publish self -> fully self-contained, no runtime needed (~70 MB)
echo ============================================================
echo.

if /i "%~1"=="self" (
    echo [mode] self-contained single file
    dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%OUTDIR%"
) else (
    echo [mode] framework-dependent
    dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained false -o "%OUTDIR%"
)
if errorlevel 1 goto :fail

echo.
echo [OK] server published:
echo      %OUTDIR%\PersonalHub.Server.exe
echo      version %PH_VERSION%
echo.
echo      Check what you shipped with:
echo        "%OUTDIR%\PersonalHub.Server.exe" --version
echo.
echo      The db file is resolved at startup: the exe walks up until it finds
echo      PersonalHub.sln, so a publish inside this repo still uses
echo      %PH_ROOT%\personalhub.db3
echo      Copy the folder elsewhere and the db is created next to the exe,
echo      unless you set PERSONALHUB_DB to an explicit path.
echo.
pause
exit /b 0

:fail
echo.
echo [!] Publish FAILED. Scroll up for the first error.
echo.
pause
exit /b 1

:abort
echo.
pause
exit /b 1
