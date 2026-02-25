@echo off
setlocal
:: Start all containers without rebuilding anything.
:: Use rebuild-game.bat if you've changed server binaries or scripts.
::
:: Usage: mimir deploy start          (from inside the project dir)
::        start.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy start
    echo   Or:      start.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"
docker compose --profile patch -f docker-compose.yml up -d
pause
