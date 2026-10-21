@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul
title PersonalHub - install APK as an UPDATE (keeps data)
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

REM ===========================================================================
REM  install-apk.bat -- install a new build OVER the installed app.
REM
REM  WHY THIS EXISTS
REM  ---------------
REM  "Copy the APK to the phone and tap it" is NOT the same as an update:
REM    - different signing key   -> Android refuses, the only way out is
REM                                 UNINSTALL, and uninstall deletes
REM                                 /data/data/<pkg>/files/personalhub.db3
REM                                 (this already destroyed one day of data)
REM    - lower versionCode       -> refused the same way
REM  This script does the safe version: "adb install -r" -- replace in place,
REM  keep the app's private directory, keep every record.
REM
REM  USAGE
REM      install-apk.bat                     newest APK under out\android\
REM      install-apk.bat <path-to.apk>       a specific file
REM
REM  It also pulls a DB backup first when the installed build is debuggable,
REM  so even a failed upgrade cannot lose the day's entries.
REM ===========================================================================

set "PKG=com.companyname.personalhub.client"
set "APKDIR=%PH_ROOT%\out\android"
set "APK=%~1"

REM ---- pick the APK -------------------------------------------------------
if defined APK goto :haveapk
if not exist "%APKDIR%" goto :noapk

for /f "delims=" %%F in ('dir /b /o-d "%APKDIR%\PersonalHub-*.apk" 2^>nul') do (
    set "APK=%APKDIR%\%%F"
    goto :haveapk
)
for /f "delims=" %%F in ('dir /b /o-d "%APKDIR%\*.apk" 2^>nul') do (
    set "APK=%APKDIR%\%%F"
    goto :haveapk
)

:noapk
echo.
echo [!] No APK found under %APKDIR%
echo     Build one first: scripts\client-android-release.bat
echo.
goto :abort

:haveapk
if not exist "%APK%" (
    echo.
    echo [!] File not found: %APK%
    echo.
    goto :abort
)

REM ---- device -------------------------------------------------------------
where adb >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] adb not found. Install Android platform-tools, or set PATH.
    echo.
    goto :abort
)

adb devices | findstr /R /C:"device$" >nul
if errorlevel 1 (
    echo.
    echo [!] No device. Plug the phone in, unlock it, allow USB debugging.
    echo.
    goto :abort
)

echo ============================================================
echo  Update install (data is kept)
echo  APK : %APK%
for %%F in ("%APK%") do echo  size: %%~zF bytes
echo ============================================================
echo.

REM ---- what is installed now ---------------------------------------------
echo  Installed now:
for /f "tokens=*" %%L in ('adb shell dumpsys package %PKG% 2^>nul ^| findstr /C:"versionCode=" /C:"versionName="') do echo     %%L
echo.

REM ---- can we read the DB? (only debug builds allow run-as) ---------------
adb shell run-as %PKG% id >nul 2>&1
if errorlevel 1 (
    echo  DB backup: skipped ^(installed build is not debuggable^)
    echo              that is normal for a release build; the server copy is
    echo              the safety net for this one.
) else (
    echo  DB backup: pulling a copy first...
    set "PH_NO_PAUSE=1"
    call "%~dp0phone-db.bat" backup
    echo.
)

REM ---- the actual install ------------------------------------------------
echo  Installing over the top ^(-r = replace, keep data^)...
echo.
adb install -r -d "%APK%" > "%TEMP%\ph-install.log" 2>&1
type "%TEMP%\ph-install.log"
echo.

findstr /C:"Success" "%TEMP%\ph-install.log" >nul
if errorlevel 1 goto :failed

echo ============================================================
echo  [OK] Updated in place. Every record is still on the phone.
echo ============================================================
for /f "tokens=*" %%L in ('adb shell dumpsys package %PKG% 2^>nul ^| findstr /C:"versionCode=" /C:"versionName="') do echo     %%L
echo.
echo  If the app shows fewer records than expected, open
echo  Overview -^> "Data ^& restore" and pull them back from the server.
echo.
goto :done

:failed
echo ============================================================
echo  [X] Install FAILED.
echo ============================================================
findstr /C:"INSTALL_FAILED_UPDATE_INCOMPATIBLE" "%TEMP%\ph-install.log" >nul
if not errorlevel 1 (
    echo.
    echo  Reason: the installed app was signed with a DIFFERENT key
    echo          than this APK. Same developer name, different cert --
    echo          Android treats them as two unrelated apps.
    echo.
    echo  Android offers exactly one way out: uninstall, then install.
    echo  Uninstall DELETES the private directory, so the local database
    echo  goes with it. Before doing that:
    echo.
    echo    1^) scripts\phone-db.bat backup     ^(works only if the
    echo       installed build is debuggable^)
    echo    2^) or make sure everything is uploaded: open the app once
    echo       while the server is reachable.
    echo    3^) after reinstalling, open Overview -^> "Data ^& restore"
    echo       and pull the history back.
    echo.
    echo  To stop this from ever happening again: build with
    echo  scripts\client-android-release.bat only, and never regenerate
    echo  the release keystore ^(see scripts\create-keystore.bat^).
    echo.
    goto :done
)

findstr /C:"INSTALL_FAILED_VERSION_DOWNGRADE" "%TEMP%\ph-install.log" >nul
if not errorlevel 1 (
    echo.
    echo  Reason: this APK's versionCode is LOWER than the installed one.
    echo          Bump the version first: scripts\version-bump.bat patch
    echo.
    goto :done
)

echo  Full log: %TEMP%\ph-install.log
echo.

:done
if not defined PH_NO_PAUSE pause
exit /b 0

:abort
if not defined PH_NO_PAUSE pause
exit /b 1
