@echo off
chcp 65001 >nul
title PersonalHub Server
set EXE=%~dp0PersonalHub.Server\bin\Debug\net8.0\PersonalHub.Server.exe

if not exist "%EXE%" (
    echo [!] Server exe not found. Building...
    dotnet build "%~dp0PersonalHub.Server\PersonalHub.Server.csproj" -c Debug
    if not exist "%EXE%" (
        echo [!] Build failed. Aborting.
        pause
        exit /b 1
    )
)

echo ============================================================
echo  PersonalHub Server
echo ------------------------------------------------------------
echo  Keep this window OPEN. Closing it will stop the server.
echo  The phone must be on the SAME Wi-Fi / LAN as this PC.
echo  Listening on TCP port 5000.
echo ============================================================
echo.

"%EXE%"

echo.
echo Server stopped.
pause
