@echo off
setlocal
:: Rebuild all container images and start containers.
:: SQL data is preserved (only rebuild-sql wipes the volume).
::
:: Usage: mimir deploy rebuild-game          (from inside the project dir)
::        rebuild-game.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy rebuild-game
    echo   Or:      rebuild-game.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"
docker compose --profile patch -f docker-compose.yml build sqlserver account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
docker compose --profile patch -f docker-compose.yml up -d
pause
