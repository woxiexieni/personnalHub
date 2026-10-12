@echo off
REM ============================================================================
REM build.bat -- run ProtoGen to compile Protos/*.proto into C# code
REM
REM Usage: run this file in the project root: .\build.bat
REM Prereq: protoc.exe / ProtoGen.exe must exist at D:\work\src\develop\Proto\
REM ============================================================================

setlocal

REM ---- path config (edit here if your toolchain is in a different place) ----
set PROTO_TOOL_DIR=.\Protos
set PROTOGEN=%PROTO_TOOL_DIR%\ProtoGen.exe
set OUTPUT_DIR=.\Generated
set PROTO_DIR=protos

REM ---- 1. make sure Generated/ exists, clean old output ----
if not exist %OUTPUT_DIR% mkdir %OUTPUT_DIR%
if exist %OUTPUT_DIR%\*.cs del /q %OUTPUT_DIR%\*.cs

REM ---- 2. expand *.proto and run ProtoGen per file ----
echo === ProtoGen running ===
echo    tool:        %PROTOGEN%
echo    proto src:    %PROTO_DIR%\*.proto
echo    output dir:   %OUTPUT_DIR%
echo.

REM for loop expands the glob *inside* cmd, so this works under both cmd and PowerShell
set PROTO_FILES=
for %%f in (%PROTO_DIR%\*.proto) do (
    echo Processing: %%f
    "%PROTOGEN%" ^
      --protoc_dir="%PROTO_TOOL_DIR%" ^
      -output_directory="%OUTPUT_DIR%" ^
      -cls_compliance=true ^
      -file_extension=.cs ^
      -line_break=Windows ^
      "%%f"
    if errorlevel 1 (
        echo.
        echo *** ProtoGen FAILED on file: %%f ***
        exit /b 1
    )
)

echo.
echo === Done. Generated files in: %OUTPUT_DIR% ===
echo Next step: open ProtobufDemo.sln in VS and press F5.
endlocal
