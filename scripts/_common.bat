@echo off
REM ===========================================================================
REM  _common.bat -- shared helpers, not meant to be run directly.
REM
REM  Usage at the top of another script:
REM      call "%~dp0_common.bat"
REM      if errorlevel 1 goto :abort
REM
REM  Provides:
REM      PH_ROOT    = repo root (the folder that contains PersonalHub.sln)
REM      PH_VERSION = version read from <root>\version.txt, e.g. 1.0.0
REM      PH_CODE    = android versionCode derived from PH_VERSION, e.g. 10000
REM
REM  NOTE: every .bat in this folder is intentionally ASCII-only. cmd.exe reads
REM  .bat files with the console code page (GBK on Chinese Windows), so UTF-8
REM  Chinese characters turn into garbage bytes and can break the commands.
REM  All Chinese text is printed by the .NET programs themselves.
REM ===========================================================================

REM "%~dp0" always ends with a backslash and points at this file's folder,
REM so "%~dp0.." is the repo root no matter where the repo is checked out.
for %%I in ("%~dp0..") do set "PH_ROOT=%%~fI"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo.
    echo [!] "dotnet" was not found in PATH.
    echo     Install the .NET 8 SDK first: https://dotnet.microsoft.com/download
    echo     Then close this window and run the script again.
    echo.
    exit /b 1
)

if not exist "%PH_ROOT%\PersonalHub.sln" (
    echo.
    echo [!] PersonalHub.sln was not found under:
    echo     %PH_ROOT%
    echo     Keep this "scripts" folder directly inside the repo root.
    echo.
    exit /b 1
)

REM ---- current version (single source of truth: <root>\version.txt) ----
REM The .NET build reads the same file via Directory.Build.props, so what this
REM script prints is exactly what ends up inside the APK / the server exe.
set "PH_VERSION="
if exist "%PH_ROOT%\version.txt" for /f "usebackq delims=" %%v in ("%PH_ROOT%\version.txt") do set "PH_VERSION=%%v"
if not defined PH_VERSION set "PH_VERSION=1.0.0"

set "PH_CODE="
for /f "tokens=1,2,3 delims=." %%a in ("%PH_VERSION%") do set /a PH_CODE=%%a*10000+%%b*100+%%c

REM ---- locate adb (platform-tools) -----------------------------------------
REM adb is almost never on PATH on a Windows dev box: Visual Studio installs it
REM under the Android SDK and only uses the full path internally. The old
REM scripts all failed with "adb was not found" because of that, so the search
REM lives here once and every script keeps calling plain "adb".
where adb >nul 2>&1
if not errorlevel 1 goto :adb_done

for %%P in (
    "%LOCALAPPDATA%\Android\Sdk\platform-tools"
    "C:\Program Files (x86)\Android\android-sdk\platform-tools"
    "C:\Program Files\Android\android-sdk\platform-tools"
) do (
    if exist "%%~P\adb.exe" (
        set "PATH=%%~P;%PATH%"
        goto :adb_done
    )
)

:adb_done
exit /b 0
