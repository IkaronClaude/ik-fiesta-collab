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
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
if /i "%MIMIR_OS%"=="linux" (
    set "COMPOSE_FILE=docker-compose.linux.yml"
) else (
    set "COMPOSE_FILE=docker-compose.yml"
    set "DOCKER_BUILDKIT=0"
)
cd /d "%~dp0"
docker compose -f %COMPOSE_FILE% build sqlserver
docker compose -f %COMPOSE_FILE% up -d sqlserver

:: SA_PASSWORD is preserved — setup-sql.ps1 handles password sync on startup.
:: If you need to change the password, run: mimir deploy set-sql-password NEW_PASSWORD
echo SQL Server container rebuilt. SA_PASSWORD from deploy config will be applied on startup.
echo If you need to change the password, run: mimir deploy set-sql-password NEW_PASSWORD
