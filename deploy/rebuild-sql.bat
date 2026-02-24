@echo off
:: Rebuild the SQL image and wipe the database volume for a clean restore.
:: WARNING: This deletes all database data. Only use for initial setup or after .bak changes.
::
:: Usage: mimir deploy rebuild-sql          (from inside the project dir)
::        rebuild-sql.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy rebuild-sql
    echo   Or:      rebuild-sql.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
set COMPOSE_PROJECT_NAME=%PROJECT%
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"
echo WARNING: This will delete all SQL data for project '%PROJECT%' and restore from .bak files.
set /p CONFIRM="Are you sure? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Cancelled.
    pause
    exit /b 0
)
docker compose -f docker-compose.yml down -v
docker compose -f docker-compose.yml build sqlserver
docker compose -f docker-compose.yml up -d sqlserver
pause
