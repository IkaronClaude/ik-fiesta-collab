@echo off
setlocal
:: Rebuild the SQL image and restart the SQL container.
:: Database data is PRESERVED (stored in the sql-data volume).
:: Use wipe-sql to destroy all data and restore from .bak files.
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
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"
docker compose -f docker-compose.yml build sqlserver
docker compose -f docker-compose.yml up -d sqlserver
pause
