@echo off
if /i "%1"=="deploy" goto :deploy_cmd
dotnet run --project "%~dp0src/Mimir.Cli" -- %*
exit /b %errorlevel%

:deploy_cmd
if "%2"=="" (
    echo Usage: mimir deploy ^<script^>
    echo Available: server, update, restart-game, start, stop, logs, rebuild-game, rebuild-sql, wipe-sql, reimport, set, get, clear, list, get-connection-string, set-sql-password, api, webapp
    exit /b 1
)
set "DEPLOY_BAT=%~dp0deploy\%2.bat"
if not exist "%DEPLOY_BAT%" (
    echo ERROR: No deploy script found: %DEPLOY_BAT%
    exit /b 1
)

:: Walk up from CWD to find the nearest mimir.json
set "MIMIR_PROJ_DIR=%CD%"
:findproj
if exist "%MIMIR_PROJ_DIR%\mimir.json" goto :foundproj
for %%D in ("%MIMIR_PROJ_DIR%\..") do set "MIMIR_PARENT=%%~fD"
if /i "%MIMIR_PARENT%"=="%MIMIR_PROJ_DIR%" (
    echo ERROR: mimir.json not found. Run 'mimir deploy' from inside a Mimir project directory.
    exit /b 1
)
set "MIMIR_PROJ_DIR=%MIMIR_PARENT%"
goto :findproj
:foundproj
for %%N in ("%MIMIR_PROJ_DIR%") do set "MIMIR_PROJ_NAME=%%~nxN"

:: Load per-project deploy config into environment
set "MIMIR_ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if exist "%MIMIR_ENV_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_ENV_FILE%") do set "%%K=%%L"
)

call "%DEPLOY_BAT%" "%MIMIR_PROJ_NAME%" %3 %4 %5 %6 %7 %8 %9
exit /b %errorlevel%
