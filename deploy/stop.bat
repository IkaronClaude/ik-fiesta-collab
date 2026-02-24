@echo off
:: Stop all containers. Data is preserved in the sql-data volume.
::
:: Usage: mimir deploy stop          (from inside the project dir)
::        stop.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy stop
    echo   Or:      stop.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
set COMPOSE_PROJECT_NAME=%PROJECT%
set PROJECT_NAME=%PROJECT%
cd /d "%~dp0"
docker compose --profile patch -f docker-compose.yml down
pause
