@echo off
setlocal
chcp 65001 >nul
title PersonalHub - build Android APK (Debug)
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "PROJ=%PH_ROOT%\PersonalHub.Client\PersonalHub.Client.csproj"
set "APK=%PH_ROOT%\PersonalHub.Client\bin\Debug\net8.0-android\com.companyname.personalhub.client-Signed.apk"

echo ============================================================
echo  Build Android APK  (Debug, arm64)
echo  version %PH_VERSION%  ^(versionCode %PH_CODE%^)
echo  first build can take 2-4 min, later ones ~1 min
echo ============================================================
echo.

dotnet build "%PROJ%" -f net8.0-android -c Debug
if errorlevel 1 goto :fail

echo.
echo [OK] APK: %APK%
for %%F in ("%APK%") do echo      size: %%~zF bytes

REM ---- try to push it to a phone that is plugged in over USB ----
where adb >nul 2>&1
if errorlevel 1 (
    echo.
    echo [i] adb is not in PATH - copy the APK to the phone by hand.
    goto :done
)

adb devices | findstr /R /C:"device$" >nul
if errorlevel 1 (
    echo.
    echo [i] no device shown by "adb devices" - skipping install.
    goto :done
)

echo.
echo [i] installing to the connected device...
adb install -r "%APK%"
if errorlevel 1 (
    echo.
    echo [!] install failed. If the signatures differ, uninstall first:
    echo     adb uninstall com.companyname.personalhub.client
)

:done
echo.
pause
exit /b 0

:fail
echo.
echo [!] Build FAILED. Scroll up to the first "error CSxxxx" line.
echo     Common causes: another build still running, or a missing MAUI workload
echo     ^(fix with: dotnet workload install maui-android^).
echo.
pause
exit /b 1

:abort
echo.
pause
exit /b 1
