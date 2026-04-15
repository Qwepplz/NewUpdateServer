@echo off
setlocal
cd /d "%~dp0"

set "VERSION=1.0.1"
for %%I in ("%~dp0..") do set "ROOT_DIR=%%~fI"
set "LOCAL_ROOT=%ROOT_DIR%\local"
set "DOTNET_CLI_HOME=%LOCAL_ROOT%\dotnet-home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH=0"
set "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1"
set "DOTNET_NOLOGO=1"
set "PROJECT_FILE=%ROOT_DIR%\src\UpdateServer\UpdateServer.csproj"
set "OUT_DIR=%LOCAL_ROOT%\dist"
set "BIN_FILE=%LOCAL_ROOT%\build\UpdateServer\bin\Release\net48\UpdateServer.exe"
set "OUT_FILE=%OUT_DIR%\UpdateServer.exe"
set "DOTNET=dotnet"
set "SIGN_CERT="
set "SIGN_PASSWORD="
set "SIGNTOOL="
set "TIMESTAMP_URL=http://timestamp.digicert.com"
set "NO_PAUSE=%BUILD_NO_PAUSE%"
set "EXIT_CODE=0"

:parse_args
if "%~1"=="" goto args_done
if /i "%~1"=="--help" goto usage
if /i "%~1"=="/?" goto usage
if /i "%~1"=="--no-pause" (
    set "NO_PAUSE=1"
    shift
    goto parse_args
)
if not defined SIGN_CERT (
    set "SIGN_CERT=%~1"
    shift
    goto parse_args
)
if not defined SIGN_PASSWORD (
    set "SIGN_PASSWORD=%~1"
    shift
    goto parse_args
)
echo Unknown argument: %~1
set "EXIT_CODE=1"
goto finish

:args_done
if not exist "%PROJECT_FILE%" (
    echo Project file was not found:
    echo %PROJECT_FILE%
    set "EXIT_CODE=1"
    goto finish
)

where %DOTNET% >nul 2>nul
if errorlevel 1 (
    echo dotnet SDK was not found.
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%LOCAL_ROOT%" mkdir "%LOCAL_ROOT%"
if not exist "%OUT_DIR%" mkdir "%OUT_DIR%"
if not exist "%DOTNET_CLI_HOME%" mkdir "%DOTNET_CLI_HOME%"

echo Building UpdateServer v%VERSION%...
%DOTNET% build "%PROJECT_FILE%" -c Release --nologo

set "EXIT_CODE=%ERRORLEVEL%"
if not "%EXIT_CODE%"=="0" (
    echo Build failed. Exit code: %EXIT_CODE%
    goto finish
)

if not exist "%BIN_FILE%" (
    echo Build output was not found:
    echo %BIN_FILE%
    set "EXIT_CODE=1"
    goto finish
)

copy /Y "%BIN_FILE%" "%OUT_FILE%" >nul
if errorlevel 1 (
    echo Cannot copy build output to dist.
    set "EXIT_CODE=1"
    goto finish
)

echo Build completed: %OUT_FILE%

if not defined SIGN_CERT (
    echo Signing skipped. Pass a .pfx certificate path to sign the EXE.
    goto finish
)

call :find_signtool
if not defined SIGNTOOL (
    echo SignTool was not found. Install Windows SDK or add signtool.exe to PATH.
    set "EXIT_CODE=1"
    goto finish
)

if not exist "%SIGN_CERT%" (
    echo Signing certificate was not found:
    echo %SIGN_CERT%
    set "EXIT_CODE=1"
    goto finish
)

echo Signing with: %SIGN_CERT%
if defined SIGN_PASSWORD (
    "%SIGNTOOL%" sign /fd SHA256 /tr "%TIMESTAMP_URL%" /td SHA256 /f "%SIGN_CERT%" /p "%SIGN_PASSWORD%" "%OUT_FILE%"
) else (
    "%SIGNTOOL%" sign /fd SHA256 /tr "%TIMESTAMP_URL%" /td SHA256 /f "%SIGN_CERT%" "%OUT_FILE%"
)

set "EXIT_CODE=%ERRORLEVEL%"
if "%EXIT_CODE%"=="0" (
    echo Signing completed.
) else (
    echo Signing failed. Exit code: %EXIT_CODE%
)
goto finish

:find_signtool
for %%I in (signtool.exe) do if not "%%~$PATH:I"=="" set "SIGNTOOL=%%~$PATH:I"
if defined SIGNTOOL exit /b 0

for %%I in ("%ProgramFiles(x86)%\Windows Kits\10\bin\*\x64\signtool.exe") do (
    if exist "%%~fI" set "SIGNTOOL=%%~fI"
)
if defined SIGNTOOL exit /b 0

for %%I in ("%ProgramFiles(x86)%\Windows Kits\10\bin\*\x86\signtool.exe") do (
    if exist "%%~fI" set "SIGNTOOL=%%~fI"
)
if defined SIGNTOOL exit /b 0

if exist "%ProgramFiles(x86)%\Windows Kits\8.1\bin\x64\signtool.exe" (
    set "SIGNTOOL=%ProgramFiles(x86)%\Windows Kits\8.1\bin\x64\signtool.exe"
)
exit /b 0

:usage
echo Usage:
echo   Build-UpdateServer.bat [--no-pause] [cert.pfx] [password]
echo.
echo Examples:
echo   Build-UpdateServer.bat
echo   Build-UpdateServer.bat --no-pause
echo   Build-UpdateServer.bat "C:\certs\code-signing.pfx" "password"
echo.
set "EXIT_CODE=0"
goto finish

:finish
echo.
if not defined NO_PAUSE pause
exit /b %EXIT_CODE%
