@echo off
setlocal
:: ================================================================
:: mimir deploy list
:: Lists all deploy config variables for the current project.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy list
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if not exist "%ENV_FILE%" (
    echo No deploy config found for %PROJECT% ^(%ENV_FILE%^).
    echo   Set a variable with: mimir deploy set KEY=VALUE
    exit /b 0
)
echo Deploy config for %PROJECT%:
echo   ^(%ENV_FILE%^)
echo.
type "%ENV_FILE%"
