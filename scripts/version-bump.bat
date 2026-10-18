@echo off
setlocal enabledelayedexpansion
chcp 65001 >nul
title PersonalHub - version
call "%~dp0_common.bat"
if errorlevel 1 goto :abort

set "VERFILE=%PH_ROOT%\version.txt"

if "%~1"=="" goto :show
if /i "%~1"=="show" goto :show

REM ---------------------------------------------------------------- new value
set "NEW="
if /i "%~1"=="major" goto :bump
if /i "%~1"=="minor" goto :bump
if /i "%~1"=="patch" goto :bump

REM explicit "x.y.z"
echo %~1| findstr /r /c:"^[0-9][0-9]*\.[0-9][0-9]*\.[0-9][0-9]*$" >nul
if errorlevel 1 goto :badarg
set "NEW=%~1"
goto :write

:bump
for /f "tokens=1,2,3 delims=." %%a in ("%PH_VERSION%") do (
    set /a "MAJ=%%a" & set /a "MIN=%%b" & set /a "PAT=%%c"
)
if /i "%~1"=="major" ( set /a MAJ+=1 & set /a MIN=0 & set /a PAT=0 )
if /i "%~1"=="minor" ( set /a MIN+=1 & set /a PAT=0 )
if /i "%~1"=="patch" ( set /a PAT+=1 )
set "NEW=!MAJ!.!MIN!.!PAT!"
goto :write

:write
REM sanity: minor / patch must stay below 100, otherwise versionCode collides
for /f "tokens=1,2,3 delims=." %%a in ("%NEW%") do (
    if %%b GEQ 100 goto :toobig
    if %%c GEQ 100 goto :toobig
    set /a "NEWCODE=%%a*10000+%%b*100+%%c"
)
if !NEWCODE! LEQ !PH_CODE! (
    echo.
    echo [!] refused: %NEW% maps to versionCode !NEWCODE!, which is not greater
    echo     than the current %PH_VERSION% ^(versionCode %PH_CODE%^).
    echo     Android only accepts an install whose versionCode never goes down.
    echo.
    goto :fail
)

REM write WITHOUT trailing spaces; the .NET build trims anyway
> "%VERFILE%" (
    <nul set /p "=%NEW%"
    echo.
)

echo.
echo [OK] version %PH_VERSION%  -^>  %NEW%
echo      android versionCode: %PH_CODE% -^> !NEWCODE!
echo      file: %VERFILE%

if /i "%~2"=="tag" (
    echo.
    echo [i] git tag v%NEW%
    git -C "%PH_ROOT%" tag v%NEW%
    if errorlevel 1 (
        echo [!] tag failed - maybe it already exists. See: git -C "%PH_ROOT%" tag
    ) else (
        echo      done. push it with: git push --tags
    )
) else (
    echo.
    echo [i] next step: build the APK, or tag this release:
    echo     scripts\version-bump.bat show
    echo     git tag v%NEW%  ^&^&  git push --tags
)
echo.
pause
exit /b 0

:show
echo.
echo ============================================================
echo  current version : %PH_VERSION%
echo  android code    : %PH_CODE%   ^(versionCode inside the APK^)
echo  source file     : %VERFILE%
echo ------------------------------------------------------------
echo  how to change it:
echo     version-bump.bat patch           1.0.0 -^> 1.0.1
echo     version-bump.bat minor           1.0.1 -^> 1.1.0
echo     version-bump.bat major           1.1.0 -^> 2.0.0
echo     version-bump.bat 1.4.2           set exactly
echo     version-bump.bat patch tag       also create git tag v1.0.1
echo ============================================================
echo.
pause
exit /b 0

:toobig
echo.
echo [!] the second and third number must stay between 0 and 99,
echo     because 1.4.100 and 1.5.0 would produce the same versionCode.
echo.
goto :fail

:badarg
echo.
echo [!] "%1" is not a valid version.
echo     use: version-bump.bat [major^|minor^|patch^|X.Y.Z]
echo.
goto :fail

:fail
echo.
pause
exit /b 1

:abort
echo.
pause
exit /b 1
