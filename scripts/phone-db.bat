@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
title PersonalHub - phone database backup / restore
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

REM ===========================================================================
REM  phone-db.bat -- pull / push the on-device SQLite database over adb.
REM
REM  WHY THIS EXISTS
REM  ---------------
REM  The app keeps everything in one SQLite file inside its private directory.
REM  Android deletes that directory the moment the app is UNINSTALLED. So the
REM  only safe way to update the app is a COVER install (same signing key) --
REM  and the only way to survive a mistake is having a copy of this file.
REM
REM      phone-db.bat backup                 pull the DB to backups\phone\
REM      phone-db.bat restore <file>         push <file> back onto the phone
REM
REM  Requires: the installed app is DEBUGGABLE (a Debug build). "run-as" is
REM  refused on release builds, which is why you must back up BEFORE replacing
REM  a Debug package with a Release one.
REM
REM  All three files are copied, not just the main one: SQLite keeps recent
REM  writes in -wal until a checkpoint, so restoring only personalhub.db3 can
REM  silently lose everything recorded since the last checkpoint.
REM ===========================================================================

set "PKG=com.companyname.personalhub.client"
set "DB=personalhub.db3"
set "OUTDIR=%PH_ROOT%\backups\phone"

where adb >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] adb was not found in PATH.
    echo     Install the Android platform-tools, or run this from a shell
    echo     that already has them.
    echo.
    goto :abort
)

adb devices | findstr /R /C:"device$" >nul
if errorlevel 1 (
    echo.
    echo [!] No device visible in "adb devices".
    echo     Plug the phone in, allow USB debugging, then try again.
    echo.
    goto :abort
)

adb shell pm list packages 2>nul | findstr /R /C:"package:%PKG%$" >nul
if errorlevel 1 (
    echo.
    echo [!] %PKG% is not installed on this device - nothing to back up.
    echo.
    goto :abort
)

if /i "%~1"=="restore" goto :restore
if /i "%~1"=="backup"  goto :backup

echo.
echo  Usage:
echo      phone-db.bat backup              pull the DB to backups\phone\
echo      phone-db.bat restore ^<file^>     push a backup back onto the phone
echo.
goto :abort

REM ------------------------------------------------------------------ backup
:backup
for /f %%t in ('powershell -NoProfile -Command "Get-Date -f yyyyMMdd-HHmmss"') do set "TS=%%t"
if not exist "%OUTDIR%" mkdir "%OUTDIR%"
set "BASE=%OUTDIR%\%DB%-%TS%"

echo.
echo  Backing up  %PKG%
echo         to  %BASE%

REM Stop the app first: a write landing mid-copy gives a torn file.
adb shell am force-stop %PKG% >nul 2>&1
for %%W in ("%DB%" "%DB%-wal" "%DB%-shm") do (
    adb exec-out run-as %PKG% cat files/%%~W > "%BASE%%%~xW" 2>nul
)

set "GOT=0"
if exist "%BASE%" for %%F in ("%BASE%") do if %%~zF GEQ 8192 set "GOT=1"
if "%GOT%"=="0" (
    echo.
    echo [!] The pull came back empty or tiny.
    echo     Most likely the installed build is NOT debuggable, so "run-as"
    echo     was refused. A Debug build is required for this to work.
    echo.
    if exist "%BASE%" del /q "%BASE%" >nul 2>&1
    goto :abort
)

copy /y "%BASE%" "%OUTDIR%\%DB%-latest" >nul
echo.
echo [OK] Saved:
for %%W in ("%DB%" "%DB%-wal" "%DB%-shm") do (
    if exist "%BASE%%%~xW" for %%F in ("%BASE%%%~xW") do echo      %%~nxF   %%~zF bytes
)
echo.
echo  Latest copy: %OUTDIR%\%DB%-latest
echo  Restore with: phone-db.bat restore "%BASE%"
echo.
goto :done

REM ----------------------------------------------------------------- restore
:restore
set "SRC=%~2"
if not defined SRC (
    echo.
    echo [!] Missing file. Usage: phone-db.bat restore ^<file^>
    echo     Recent backups live in: %OUTDIR%
    echo.
    goto :abort
)
if not exist "%SRC%" (
    echo.
    echo [!] File not found: %SRC%
    echo.
    goto :abort
)

echo.
echo  Restoring  %SRC%
echo        to  %PKG%  (overwrites whatever is on the phone now)

adb shell am force-stop %PKG% >nul 2>&1
adb push "%SRC%" /data/local/tmp/%DB% >nul
if errorlevel 1 (
    echo.
    echo [!] adb push failed.
    echo.
    goto :abort
)

adb shell run-as %PKG% cp /data/local/tmp/%DB% files/%DB%
if errorlevel 1 (
    echo.
    echo [!] Could not copy into the app's private directory.
    echo     The installed build is probably not debuggable.
    echo.
    adb shell rm /data/local/tmp/%DB% >nul 2>&1
    goto :abort
)

REM Drop sidecar files: they belong to the OLD database and would be replayed
REM on top of the restored one.
adb shell run-as %PKG% rm -f files/%DB%-wal >nul 2>&1
adb shell run-as %PKG% rm -f files/%DB%-shm >nul 2>&1
adb shell rm /data/local/tmp/%DB% >nul 2>&1

echo.
echo [OK] Restored. Open the app and give it a moment to reconnect.
echo.
goto :done

:done
if not defined PH_NO_PAUSE pause
exit /b 0

:abort
if not defined PH_NO_PAUSE pause
exit /b 1
