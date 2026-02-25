@echo off
setlocal

if "%~1"=="" (
    echo ERROR: Project name required.
    echo Usage: api.bat ^<project-name^>
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"

dotnet publish "%~dp0..\src\Mimir.Api" -c Release -o "%~dp0api-publish" --no-self-contained
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    pause
    exit /b 1
)

docker compose -f docker-compose.yml --profile api build api
docker compose -f docker-compose.yml --profile api up -d api
