@echo off
setlocal
chcp 65001 >nul
title PersonalHub - service control
call "%~dp0_common.bat"
if errorlevel 1 goto :fail

REM ===========================================================================
REM  PersonalHub Server as a Windows service.
REM
REM  Why a service instead of a startup shortcut:
REM    - starts at boot, before anyone logs in (a login-time shortcut needs a user)
REM    - no console window you can close by accident
REM    - SCM restarts it if it crashes  (see "sc failure" in :install)
REM    - shows up next to the other services in Task Manager
REM
REM  The service runs the PUBLISHED exe in out\server, NOT bin\Debug.
REM  That matters: a normal "dotnet build" then never hits a locked file,
REM  and shipping new code is just:
REM        server-service.bat update
REM
REM  Commands:
REM    install     create + start the service (admin)
REM    uninstall   stop + delete the service (admin)
REM    start       start it (admin)
REM    stop        stop it (admin)
REM    restart     stop then start (admin)
REM    update      publish latest code, then restart (admin)
REM    status      is it installed / running / which ports (no admin)
REM    logs        open the log folder (no admin)
REM ===========================================================================

set "SVC=PersonalHubServer"
set "SVC_DISPLAY=PersonalHub Server"
set "PUBDIR=%PH_ROOT%\out\server"
set "EXE=%PUBDIR%\PersonalHub.Server.exe"
set "GRPC_PORT=5000"
set "WEB_PORT=5001"

set "CMD=%~1"

REM ---- are we elevated? single source of truth for the whole script ----
REM  fltmc needs admin and does not depend on any service; "net session" needs
REM  admin too but ALSO needs the Server service running, so it is only a
REM  fallback for the rare box where fltmc is missing.
set "PH_IS_ADMIN="
fltmc >nul 2>&1
if not errorlevel 1 set "PH_IS_ADMIN=1"
if not defined PH_IS_ADMIN (
    net session >nul 2>&1
    if not errorlevel 1 set "PH_IS_ADMIN=1"
)

REM ---- no argument: elevated -> install (the usual intent), otherwise show help ----
if "%CMD%"=="" (
    if defined PH_IS_ADMIN (set "CMD=install") else (set "CMD=usage")
)

echo ============================================================
echo  PersonalHub service - %CMD%
echo  service name : %SVC%
echo  exe          : %EXE%
echo  version      : %PH_VERSION%
echo ============================================================
echo.

REM ---- admin check only for the commands that touch the SCM ----
set "NEED_ADMIN="
for %%C in (install uninstall start stop restart update) do (
    if /i "%CMD%"=="%%C" set "NEED_ADMIN=1"
)
if defined NEED_ADMIN if not defined PH_IS_ADMIN (
    echo [!] Administrator rights are required for "%CMD%".
    echo.
    echo     Close this window, then RIGHT-CLICK server-service.bat
    echo     and choose "Run as administrator".
    echo.
    goto :fail
)

if /i "%CMD%"=="install"   goto :install
if /i "%CMD%"=="uninstall" goto :uninstall
if /i "%CMD%"=="start"     goto :start
if /i "%CMD%"=="stop"      goto :stop
if /i "%CMD%"=="restart"   goto :restart
if /i "%CMD%"=="update"    goto :update
if /i "%CMD%"=="status"    goto :status
if /i "%CMD%"=="logs"      goto :logs
if /i "%CMD%"=="usage"     goto :usage

echo [!] Unknown command: %CMD%
echo.
echo     install ^| uninstall ^| start ^| stop ^| restart ^| update ^| status ^| logs
echo.
goto :fail

REM ===========================================================================
:usage
echo Run this script with one of these commands:
echo.
echo     install     create + start the service, auto-start at boot   [admin]
echo     update      publish latest code, then restart the service    [admin]
echo     restart     stop then start                                  [admin]
echo     stop        stop it                                          [admin]
echo     start       start it                                         [admin]
echo     uninstall   stop + remove the service (keeps db and logs)    [admin]
echo     status      installed? running? which ports?                 [no admin]
echo     logs        open the log folder                              [no admin]
echo.
echo HOW TO RUN:
echo   1. For [admin] commands: close this window, RIGHT-CLICK this .bat,
echo      choose "Run as administrator", and type the command you want.
echo      Running it with no argument as administrator = install.
echo   2. status / logs work from a normal double-click.
echo.
goto :done

REM ===========================================================================
:install
sc.exe query "%SVC%" >nul 2>&1
if not errorlevel 1 (
    echo [!] Service "%SVC%" is already installed.
    echo.
    echo     To pick up new code :  server-service.bat update
    echo     To restart it       :  server-service.bat restart
    echo     To remove it        :  server-service.bat uninstall
    echo.
    goto :fail
)

if not exist "%EXE%" (
    echo [*] No published exe found - publishing first...
    echo.
    call :publish
    if errorlevel 1 goto :fail
    echo.
)

echo %EXE% | find " " >nul
if not errorlevel 1 (
    echo [!] The repo path contains a SPACE:
    echo     %EXE%
    echo.
    echo     The service can still be created, but if it fails to start with
    echo     error 1053 / 2, move the repo to a path without spaces.
    echo.
)

REM ---- port must be free, otherwise the exe exits immediately and the ----
REM ---- SCM reports "service terminated with service-specific error"  ----
netstat -ano | findstr /C:":%GRPC_PORT% " | findstr /C:"LISTENING" >nul
if not errorlevel 1 (
    echo [!] Port %GRPC_PORT% is already in use - the service would fail to start.
    echo.
    echo     A console server is probably running. Close that window first, or:
    echo         taskkill /F /IM PersonalHub.Server.exe
    echo.
    goto :fail
)

echo [*] Creating service...
sc.exe create "%SVC%" binPath= "%EXE%" start= auto DisplayName= "%SVC_DISPLAY%"
if errorlevel 1 (
    echo.
    echo [!] sc create FAILED - see the message above.
    goto :fail
)

sc.exe description "%SVC%" "PersonalHub self-hosted server: gRPC for the phone app, web admin on port %WEB_PORT%." >nul 2>&1

REM ---- auto restart on crash: 5s, then 10s, then every 30s; counter resets daily ----
sc.exe failure "%SVC%" reset= 86400 actions= restart/5000/restart/10000/restart/30000 >nul 2>&1

echo [*] Starting service...
sc.exe start "%SVC%"
echo.

call :wait_port %GRPC_PORT% 20
if errorlevel 1 goto :start_failed

echo.
echo [OK] Service installed and running.
echo.
echo      gRPC  (phone)  : %GRPC_PORT%
echo      Admin (browser): %WEB_PORT%   --^>  http://127.0.0.1:%WEB_PORT%/
echo      Logs           : %PH_ROOT%\logs
echo.
echo      It now starts automatically at every boot.
echo      It runs as LocalSystem and uses the SAME db as the console version:
echo        %PH_ROOT%\personalhub.db3
echo      So before starting a console server for development, stop this one:
echo        server-service.bat stop
echo.
goto :done

:start_failed
echo.
echo [!] The service did not open port %GRPC_PORT% within ~20s.
echo.
sc.exe query "%SVC%"
echo.
echo     Read the reason in the log:
echo       %PH_ROOT%\logs\server-*.log
echo.
echo     Most common: port already taken by another instance.
echo.
goto :fail

REM ===========================================================================
:uninstall
sc.exe query "%SVC%" >nul 2>&1
if errorlevel 1 (
    echo [--] Service "%SVC%" is not installed - nothing to remove.
    goto :done
)

echo [*] Stopping...
sc.exe stop "%SVC%" >nul 2>&1
call :wait_state STOPPED 30

echo [*] Deleting service...
sc.exe delete "%SVC%"
if errorlevel 1 (
    echo.
    echo [!] sc delete failed. Close Task Manager's Services tab and retry.
    goto :fail
)
echo.
echo [OK] Service removed. The database and logs were NOT touched.
echo.
goto :done

REM ===========================================================================
:start
sc.exe start "%SVC%"
if errorlevel 1 goto :fail
call :wait_port %GRPC_PORT% 20
if errorlevel 1 (
    echo.
    echo [!] Started, but port %GRPC_PORT% is not listening yet.
    echo     Check: %PH_ROOT%\logs\server-*.log
    goto :fail
)
echo.
echo [OK] Running on %GRPC_PORT% (gRPC) / %WEB_PORT% (admin).
echo.
goto :done

:stop
sc.exe stop "%SVC%" >nul 2>&1
call :wait_state STOPPED 30
if errorlevel 1 (
    echo [!] Still not stopped after 30s. Current state:
    sc.exe query "%SVC%"
    goto :fail
)
echo [OK] Service stopped.
echo.
goto :done

:restart
echo [*] Stopping...
sc.exe stop "%SVC%" >nul 2>&1
call :wait_state STOPPED 30
echo [*] Starting...
sc.exe start "%SVC%"
if errorlevel 1 goto :fail
call :wait_port %GRPC_PORT% 20
if errorlevel 1 (
    echo.
    echo [!] Port %GRPC_PORT% is not listening. Check %PH_ROOT%\logs
    goto :fail
)
echo.
echo [OK] Restarted on %GRPC_PORT% (gRPC) / %WEB_PORT% (admin).
echo.
goto :done

REM ===========================================================================
:update
echo [*] Publishing latest code...
call :publish
if errorlevel 1 goto :fail
echo.

sc.exe query "%SVC%" >nul 2>&1
if errorlevel 1 (
    echo [--] Service is not installed yet. Now run:
    echo        server-service.bat install
    goto :done
)

echo [*] Stopping service (it holds the exe open)...
sc.exe stop "%SVC%" >nul 2>&1
call :wait_state STOPPED 30

echo [*] Starting service...
sc.exe start "%SVC%"
if errorlevel 1 goto :fail
call :wait_port %GRPC_PORT% 20
if errorlevel 1 (
    echo.
    echo [!] Port %GRPC_PORT% is not listening. Check %PH_ROOT%\logs
    goto :fail
)
echo.
echo [OK] Updated to %PH_VERSION% and running.
echo.
goto :done

REM ===========================================================================
:status
sc.exe query "%SVC%" >nul 2>&1
if errorlevel 1 (
    echo [--] Service "%SVC%" is NOT installed.
    echo.
    echo     Install it first - right-click, Run as administrator:
    echo        server-service.bat install
    echo     Or just run the console version:  server-run.bat
    echo.
    goto :done
)

sc.exe query "%SVC%"
echo.
sc.exe qc "%SVC%" | findstr /C:"BINARY_PATH_NAME" /C:"START_TYPE"
echo.

netstat -ano | findstr /C:":%GRPC_PORT% " | findstr /C:"LISTENING" >nul
if errorlevel 1 (echo [--] gRPC  port %GRPC_PORT% : not listening) else (echo [OK] gRPC  port %GRPC_PORT% : listening)
netstat -ano | findstr /C:":%WEB_PORT% " | findstr /C:"LISTENING" >nul
if errorlevel 1 (echo [--] Admin port %WEB_PORT% : not listening) else (echo [OK] Admin port %WEB_PORT% : listening)

echo.
echo     Admin UI: http://127.0.0.1:%WEB_PORT%/
echo.
goto :done

REM ===========================================================================
:logs
if not exist "%PH_ROOT%\logs" (
    echo [--] No logs folder yet - it is created the first time the service starts.
    goto :done
)
dir /b /o-d "%PH_ROOT%\logs\server-*.log" 2>nul
echo.
echo Opening %PH_ROOT%\logs ...
start "" "%PH_ROOT%\logs"
echo.
goto :done

REM ===========================================================================
REM  helpers
REM ===========================================================================

:publish
dotnet publish "%PH_ROOT%\PersonalHub.Server\PersonalHub.Server.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o "%PUBDIR%" --nologo -v q
if errorlevel 1 (
    echo.
    echo [!] Publish FAILED. Scroll up for the first error.
    exit /b 1
)
if not exist "%EXE%" (
    echo [!] Publish reported success but the exe is missing:
    echo     %EXE%
    exit /b 1
)
echo [OK] Published: %EXE%
exit /b 0

:wait_port
REM  %1 = port, %2 = seconds to wait
set "WP_PORT=%~1"
set /a WP_TRIES=%~2
:wait_port_loop
netstat -ano | findstr /C:":%WP_PORT% " | findstr /C:"LISTENING" >nul
if not errorlevel 1 exit /b 0
ping -n 2 127.0.0.1 >nul
set /a WP_TRIES-=1
if %WP_TRIES% GTR 0 goto :wait_port_loop
exit /b 1

:wait_state
REM  %1 = state keyword (RUNNING / STOPPED), %2 = seconds to wait
set "WS_TRIES=%~2"
:wait_state_loop
sc.exe query "%SVC%" 2>nul | findstr /C:"%~1" >nul
if not errorlevel 1 exit /b 0
ping -n 2 127.0.0.1 >nul
set /a WS_TRIES-=1
if %WS_TRIES% GTR 0 goto :wait_state_loop
exit /b 1

REM ===========================================================================
:done
echo.
if not defined PH_NO_PAUSE pause
exit /b 0

:fail
echo.
if not defined PH_NO_PAUSE pause
exit /b 1
