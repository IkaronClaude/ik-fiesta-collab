@echo off
setlocal
:: Tail container logs from inside the project dir.
:: Usage: mimir deploy tail [service]
::   e.g. mimir deploy tail account
::        mimir deploy tail           (all services)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy tail [service]
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
cd /d "%~dp0"

docker compose -f docker-compose.yml logs -f %2 %3 %4 %5 %6 %7 %8 %9
