@echo off
setlocal
:: Rebuild the game server image and start all containers.
:: Does NOT rebuild the SQL image — use rebuild-sql for that.
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
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
if /i "%MIMIR_OS%"=="linux" (
    set "COMPOSE_FILE=docker-compose.linux.yml"
) else (
    set "COMPOSE_FILE=docker-compose.yml"
    set DOCKER_BUILDKIT=0
)
cd /d "%~dp0"
docker compose --profile patch -f %COMPOSE_FILE% build account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
docker compose --profile patch -f %COMPOSE_FILE% up -d account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04 patch-server
pause
