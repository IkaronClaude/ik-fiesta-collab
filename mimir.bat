@echo off
if /i "%1"=="deploy" goto :deploy_cmd
dotnet run --project "%~dp0src/Mimir.Cli" -- %*
exit /b %errorlevel%

:deploy_cmd
if "%2"=="" (
    echo Usage: mimir deploy ^<script^>
    echo Available: server, update, restart-game, start, stop, logs, tail, rebuild-game, rebuild-sql, wipe-sql, reimport, set, get, clear, list, get-connection-string, set-sql-password, api, webapp, secret
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

:: Load per-project deploy config into environment.
:: Environment variables already set (e.g. from GitHub Actions) take precedence over the file.
set "MIMIR_ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if exist "%MIMIR_ENV_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_ENV_FILE%") do if not defined %%K set "%%K=%%L"
)

:: Load per-project secrets into environment (gitignored, never committed).
:: Environment variables already set (e.g. from GitHub Actions secrets) take precedence.
set "MIMIR_SECRETS_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.secrets"
if exist "%MIMIR_SECRETS_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_SECRETS_FILE%") do if not defined %%K set "%%K=%%L"
)

:: PORT_OFFSET: if set, offset all game/service ports by this amount.
:: Individual port vars take precedence if already set (e.g. from .mimir-deploy.env).
:: Usage: mimir deploy secret set PORT_OFFSET 100   (second server: 9110, 9113, 9116...)
if defined PORT_OFFSET (
    if not defined LOGIN_PORT  set /a "LOGIN_PORT=9010+PORT_OFFSET"
    if not defined WM_PORT     set /a "WM_PORT=9013+PORT_OFFSET"
    if not defined ZONE00_PORT set /a "ZONE00_PORT=9016+PORT_OFFSET"
    if not defined ZONE01_PORT set /a "ZONE01_PORT=9019+PORT_OFFSET"
    if not defined ZONE02_PORT set /a "ZONE02_PORT=9022+PORT_OFFSET"
    if not defined ZONE03_PORT set /a "ZONE03_PORT=9025+PORT_OFFSET"
    if not defined ZONE04_PORT set /a "ZONE04_PORT=9028+PORT_OFFSET"
    if not defined PATCH_PORT  set /a "PATCH_PORT=8080+PORT_OFFSET"
    if not defined API_PORT    set /a "API_PORT=5000+PORT_OFFSET"
    if not defined WEBAPP_PORT set /a "WEBAPP_PORT=80+PORT_OFFSET"
)

call "%DEPLOY_BAT%" "%MIMIR_PROJ_NAME%" %3 %4 %5 %6 %7 %8 %9
exit /b %errorlevel%
