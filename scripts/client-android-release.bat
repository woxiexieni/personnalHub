@echo off
setlocal
chcp 65001 >nul
title PersonalHub - publish Android APK (Release)
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "PROJ=%PH_ROOT%\PersonalHub.Client\PersonalHub.Client.csproj"
REM SRC is discovered AFTER publish (see "find the APK" below) -- it used to be
REM a fixed path here, which silently shipped a stale package on 2026-10-21.
set "MAN=%PH_ROOT%\PersonalHub.Client\obj\Release\net8.0-android\AndroidManifest.xml"
set "OUTDIR=%PH_ROOT%\out\android"

REM --------------------------------------------------------------- arguments
REM  client-android-release.bat                 use version.txt
REM  client-android-release.bat 1.4.2           force this version for this build
REM  client-android-release.bat aot             enable AOT: +~7 MB, faster cold start
REM  client-android-release.bat 1.4.2 aot       both
REM
REM  AOT is OFF by default (.NET 8 / MAUI default), which is why a plain build
REM  lands around 18.7 MiB. The old "noaot" switch is kept as a no-op alias so
REM  existing shortcuts / notes still work.
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
if /i "%~1"=="aot"   set "EXTRA=-p:RunAOTCompilation=true"
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

REM Signing key resolution - must mirror what the csproj does:
REM   local-signing.props exists -> the real release keystore
REM   otherwise                  -> the machine-local debug keystore
REM Checking only debug.keystore used to block clean machines that only had a
REM proper release key, which is exactly the setup you want for shipping.
set "SIGNPROPS=%PH_ROOT%\PersonalHub.Client\local-signing.props"
set "SIGNDESC=debug.keystore   (NOT for distribution)"
set "SIGNFAIL="
if exist "%SIGNPROPS%" (
    set "SIGNDESC=local-signing.props   (release keystore)"
) else (
    if not exist "%LocalAppData%\Xamarin\Mono for Android\debug.keystore" set "SIGNFAIL=1"
)
if defined SIGNFAIL (
    echo [!] No signing key available.
    echo     Either run scripts\create-keystore.bat once ^(real release key^),
    echo     or run client-android-debug.bat once ^(creates debug.keystore^).
    echo.
    goto :fail
)
echo  signing key : %SIGNDESC%
echo.

REM Record the start time. Used below to tell "the APK this build just made"
REM apart from "an APK somebody left behind two days ago" -- see the note there.
set "REBUILT="
set "REBUILDARG="

:attempt
for /f "usebackq delims=" %%t in (`powershell -NoProfile -Command "(Get-Date).ToUniversalTime().AddMinutes(-1).Ticks"`) do set "T0=%%t"

REM Force repackaging.
REM The APK step is skipped when the .apk file already exists and its inputs look
REM unchanged -- that is exactly the "no fresh APK" case below, and -t:Rebuild does
REM NOT help (it rebuilds the assembly, then the packaging target still sees the old
REM apk as up to date). Deleting the package-named APKs is what actually forces it.
REM Only build products are touched; the hand-kept PersonalHub-<ver>-android.apk
REM files are never matched by these patterns.
if exist "%OUTDIR%" del /q "%OUTDIR%\com.companyname.personalhub.client*.apk" >nul 2>&1
for %%D in ("%PH_ROOT%\PersonalHub.Client\bin\Release" "%PH_ROOT%\PersonalHub.Client\obj\Release") do (
    if exist "%%~D" del /s /q "%%~D\com.companyname.personalhub.client*.apk" >nul 2>&1
)

REM `build`, not `publish`.
REM publish was tried for days and kept producing surprises on this machine: the
REM assembly message pointed at out\android\ while bin\Release\...\publish\ held the
REM previous package, so the copy step shipped a stale APK (1.0.1 == 1.0.0, same md5).
REM `dotnet build -c Release` produces the signed APK in bin\Release\net8.0-android\
REM -- the location `dotnet build -getProperty:OutputPath` reports -- and the csproj
REM already carries the signing key + apk package format. One less thing to guess.
dotnet build "%PROJ%" -f net8.0-android -c Release -p:AndroidPackageFormats=apk %EXTRA% %REBUILDARG%
if errorlevel 1 goto :fail

REM ---------------------------------------------------------------- find the APK
REM WHY NOT A FIXED PATH:
REM this used to copy from bin\Release\net8.0-android\publish\. On 2026-10-21 that
REM folder still held the PREVIOUS build because MSBuild had cached a different
REM OutputPath from an earlier run -- so "PersonalHub-1.0.1-android.apk" was a
REM byte-identical copy of 1.0.0. The script printed [OK], the version check below
REM passed (it reads obj\, not the APK), and the phone would have installed the
REM old build. Silent, and only visible as "my fix did not arrive".
REM
REM So: search for a *-Signed.apk written after T0 and take the newest.
REM Where MSBuild decides to write stops mattering.
set "SRC="
for /f "usebackq delims=" %%F in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%PH_ROOT%\scripts\find-fresh-apk.ps1" -RepoRoot "%PH_ROOT%" -SinceTicks %T0%`) do set "SRC=%%F"

if defined SRC goto :found

REM Nothing new: the build was incremental ("all projects up to date") so
REM MSBuild did not repack the APK. That is normal when only the version number
REM in version.txt changed after a previous build -- retry once with -t:Rebuild,
REM which is slower but always produces a package.
if not defined REBUILT (
    set "REBUILT=1"
    set "REBUILDARG=-t:Rebuild"
    echo  [!] incremental build produced no new APK - forcing a full rebuild...
    echo.
    goto :attempt
)

echo.
echo [!] publish reported success but no fresh *-Signed.apk was found under
echo     %PH_ROOT%\PersonalHub.Client  (even after -t:Rebuild)
echo     Nothing was copied - refusing to ship a stale package.
echo     Try: dotnet clean PersonalHub.Client, then run this script again.
goto :fail

:found
echo  fresh APK  : %SRC%

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
if errorlevel 1 goto :fail

REM Same size, same md5, or the copy silently did nothing -> still a stale file.
for %%A in ("%SRC%") do for %%B in ("%DST%") do if %%~zA NEQ %%~zB goto :fail

echo.
echo [OK] signed APK copied to:
echo      %DST%
for %%F in ("%DST%") do echo      size: %%~zF bytes
echo      version %VER%  (versionCode %CODE%)
echo.

REM ------------------------------------------------- deliver to the server
REM Puts the package where the phone can fetch it over Wi-Fi, so updating stops
REM meaning "plug in USB, run adb, hit a signature mismatch".
REM
REM A failure here does NOT fail the build: the APK exists and is usable, it is
REM merely not visible on the phone yet. But it must be LOUD -- a silent success
REM line on a delivery that did not happen is exactly the 2026-10-21 incident
REM where a stale package shipped with [OK] printed above it.
echo [*] delivering to the server download folder...
powershell -NoProfile -ExecutionPolicy Bypass -File "%PH_ROOT%\scripts\publish-apk-to-server.ps1" -ApkPath "%DST%" -Version "%VER%" -Code %CODE% -UpdatesDir "%PH_ROOT%\updates" -Keep 3
if errorlevel 1 (
    echo.
    echo [!] The APK was built, but sending it to the server FAILED.
    echo     The phone will NOT see this version at http://^<this-pc-ip^>:5001/app
    echo     The package itself is fine: %DST%
    echo     Read the reason above and run this script again. It is idempotent.
    echo.
) else (
    echo [OK] the phone can now update over Wi-Fi: http://^<this-pc-ip^>:5001/app
    echo.
)
echo      Copy this single file to the phone and tap it to install.
echo      Same signing key as the Debug build, so it upgrades in place.
echo      Want a faster cold start (bigger package): client-android-release.bat aot
echo      Want a smaller package than this: not much left - see README section
echo      "why is it 18 MB" for what is inside.
echo      To bump the version: scripts\version-bump.bat patch
echo.
if not defined PH_NO_PAUSE pause
exit /b 0

:fail
echo.
echo [!] Publish FAILED. Scroll up for the first error.
echo.
if not defined PH_NO_PAUSE pause
exit /b 1

:abort
echo.
if not defined PH_NO_PAUSE pause
exit /b 1
