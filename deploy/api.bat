@echo off
setlocal

if "%~1"=="" (
    echo ERROR: Project name required.
    echo Usage: api.bat ^<project-name^>
    exit /b 1
)

set "PROJECT=%~1"

dotnet publish "%~dp0..\src\Mimir.Api" -c Release -o "%~dp0api-publish" --no-self-contained
if errorlevel 1 (
    echo ERROR: dotnet publish failed.
    exit /b 1
)

for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0

docker compose -f "%~dp0docker-compose.yml" --profile api build api
docker compose -f "%~dp0docker-compose.yml" --profile api up -d api
