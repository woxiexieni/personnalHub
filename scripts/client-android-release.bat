@echo off
setlocal
chcp 65001 >nul
title PersonalHub - publish Android APK (Release)
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "PROJ=%PH_ROOT%\PersonalHub.Client\PersonalHub.Client.csproj"
set "SRC=%PH_ROOT%\PersonalHub.Client\bin\Release\net8.0-android\publish\com.companyname.personalhub.client-Signed.apk"
set "MAN=%PH_ROOT%\PersonalHub.Client\obj\Release\net8.0-android\AndroidManifest.xml"
set "OUTDIR=%PH_ROOT%\out\android"

REM --------------------------------------------------------------- arguments
REM  client-android-release.bat                 use version.txt
REM  client-android-release.bat 1.4.2           force this version for this build
REM  client-android-release.bat noaot           skip AOT, ~6-7 MB smaller
REM  client-android-release.bat 1.4.2 noaot     both
set "VEROVERRIDE="
set "EXTRA="

if not "%~1"=="" (
    echo %~1| findstr /r /c:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
    if not errorlevel 1 (
        set "VEROVERRIDE=%~1"
        shift
    )
)
if /i "%~1"=="noaot" set "EXTRA=-p:RunAOTCompilation=false"
if defined VEROVERRIDE set "EXTRA=%EXTRA% -p:PhVersion=%VEROVERRIDE%"

set "VER=%PH_VERSION%"
set "CODE=%PH_CODE%"
if defined VEROVERRIDE (
    set "VER=%VEROVERRIDE%"
    for /f "tokens=1,2,3 delims=." %%a in ("%VEROVERRIDE%") do set /a "CODE=%%a*10000+%%b*100+%%c"
)
set "DST=%OUTDIR%\PersonalHub-%VER%-android.apk"

echo ============================================================
echo  Publish Release APK  (arm64 + trimming + assembly store)
echo  versionName : %VER%
echo  versionCode : %CODE%
echo  expected size: ~18 MB   (Debug is ~120 MB, do not ship that)
if defined EXTRA echo  extra switch: %EXTRA%
echo ============================================================
echo.

REM Release signing uses the machine-local .NET Android debug keystore that
REM PersonalHub.Client.csproj points at. It exists once you have built Debug
REM at least once on this machine; another machine may need one build first.
if not exist "%LocalAppData%\Xamarin\Mono for Android\debug.keystore" (
    echo [!] debug.keystore not found for this Windows user.
    echo     Run client-android-debug.bat once, then run this script again.
    echo     ^(or set AndroidSigningKeyStore in PersonalHub.Client.csproj^)
    echo.
    goto :fail
)

dotnet publish "%PROJ%" -f net8.0-android -c Release -p:AndroidPackageFormats=apk %EXTRA%
if errorlevel 1 goto :fail

if not exist "%SRC%" (
    echo.
    echo [!] publish reported success but the signed APK is missing:
    echo     %SRC%
    goto :fail
)

REM ---- did the version really reach the package? (catches a broken props file)
REM findstr regex: the "." stands for the quote character, so the command needs
REM no nested double quotes (cmd cannot escape them).
findstr /r /c:"versionCode=.%CODE%." "%MAN%" >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] the generated manifest does not carry versionCode=%CODE%.
    echo     Check Directory.Build.props and version.txt.
    echo     manifest: %MAN%
    echo.
    goto :fail
)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
copy /y "%SRC%" "%DST%" >nul

echo.
echo [OK] signed APK copied to:
echo      %DST%
for %%F in ("%DST%") do echo      size: %%~zF bytes
echo      version %VER%  (versionCode %CODE%)
echo.
echo      Copy this single file to the phone and tap it to install.
echo      Same signing key as the Debug build, so it upgrades in place.
echo      To shrink further: client-android-release.bat noaot
echo      To bump the version: scripts\version-bump.bat patch
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
