@echo off
setlocal
:: Tail logs from all game server containers.
::
:: Usage: mimir deploy logs          (from inside the project dir)
::        logs.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy logs
    echo   Or:      logs.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
if /i "%MIMIR_OS%"=="linux" ( set "COMPOSE_FILE=docker-compose.linux.yml" ) else ( set "COMPOSE_FILE=docker-compose.yml" )
cd /d "%~dp0"
docker compose -f %COMPOSE_FILE% logs -f account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
