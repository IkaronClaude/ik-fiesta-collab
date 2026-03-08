@echo off
setlocal

if "%~1"=="" (
    echo ERROR: Project name required.
    echo Usage: api.bat ^<project-name^>
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
:: Load deploy config (SA_PASSWORD etc.) from project env file
set "MIMIR_ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if exist "%MIMIR_ENV_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_ENV_FILE%") do set "%%K=%%L"
)
set "MIMIR_SECRETS_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.secrets"
if exist "%MIMIR_SECRETS_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_SECRETS_FILE%") do if not defined %%K set "%%K=%%L"
)
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
if not defined CERT_DIR set "CERT_DIR=%MIMIR_PROJ_DIR%\certs"
if not exist "%CERT_DIR%" mkdir "%CERT_DIR%"
if /i "%MIMIR_OS%"=="linux" ( set "COMPOSE_FILE=docker-compose.linux.yml" ) else ( set "COMPOSE_FILE=docker-compose.yml" & set DOCKER_BUILDKIT=0 )
cd /d "%~dp0"

dotnet publish "%~dp0..\src\Mimir.Api" -c Release -o "%~dp0api-publish" --no-self-contained
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)

docker compose -f %COMPOSE_FILE% --profile api build api
docker compose -f %COMPOSE_FILE% --profile api up -d api
