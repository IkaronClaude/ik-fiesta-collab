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
