@echo off
setlocal
:: Wipe the SQL data volume and restore all databases from .bak files.
:: WARNING: This DESTROYS all database data. Use for initial setup or
:: when you need a clean slate from the backup files.
::
:: Usage: mimir deploy wipe-sql          (from inside the project dir)
::        wipe-sql.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy wipe-sql
    echo   Or:      wipe-sql.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"
echo WARNING: This will DELETE ALL SQL DATA for project '%PROJECT%' and restore from .bak files.
set /p CONFIRM="Are you sure? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Cancelled.
    pause
    exit /b 0
)
docker compose -f docker-compose.yml down -v
docker compose -f docker-compose.yml build sqlserver
docker compose -f docker-compose.yml up -d sqlserver

:: Clear stored SA_PASSWORD — image is rebuilt with the default install password.
:: Run 'mimir deploy set-sql-password NEW_PASSWORD' to set a new one.
set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
set "TMP_FILE=%TEMP%\mimir-deploy-set-tmp.txt"
if exist "%ENV_FILE%" (
    findstr /v /b /c:"SA_PASSWORD=" "%ENV_FILE%" > "%TMP_FILE%" 2>nul
    move /y "%TMP_FILE%" "%ENV_FILE%" > nul
)
echo SA_PASSWORD cleared from deploy config.
echo Run 'mimir deploy set-sql-password NEW_PASSWORD' to set a new password.
pause
