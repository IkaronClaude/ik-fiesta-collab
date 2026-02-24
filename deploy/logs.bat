@echo off
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
set COMPOSE_PROJECT_NAME=%PROJECT%
set PROJECT_NAME=%PROJECT%
cd /d "%~dp0"
docker compose -f docker-compose.yml logs -f account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
