@echo off
setlocal
:: ================================================================
:: mimir deploy set-sa-password OLD_PASSWORD
:: Changes the sa password in the running SQL container to match
:: the value stored in .mimir-deploy.env (SA_PASSWORD key).
::
:: Workflow:
::   1. Set the new password in config:
::        mimir deploy set SA_PASSWORD NewPassword
::   2. Apply to the running SQL container:
::        mimir deploy set-sa-password OldPassword
::   3. Restart game servers to pick up the new password:
::        mimir deploy restart-game
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy set-sa-password OLD_PASSWORD
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy set-sa-password OLD_PASSWORD
    echo   Changes the sa password in the running SQL container.
    echo   The new password is read from .mimir-deploy.env ^(SA_PASSWORD^).
    echo.
    echo   Steps:
    echo     1. mimir deploy set SA_PASSWORD NewPassword
    echo     2. mimir deploy set-sa-password OldPassword
    echo     3. mimir deploy restart-game
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "OLD_PASSWORD=%~2"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if not exist "%ENV_FILE%" (
    echo ERROR: No deploy config found at %ENV_FILE%
    echo   Run: mimir deploy set SA_PASSWORD NewPassword
    exit /b 1
)

:: Read SA_PASSWORD from env file
set "NEW_PASSWORD="
for /f "usebackq tokens=1* delims==" %%K in ("%ENV_FILE%") do (
    if /i "%%K"=="SA_PASSWORD" set "NEW_PASSWORD=%%L"
)

if "%NEW_PASSWORD%"=="" (
    echo ERROR: SA_PASSWORD not set in deploy config.
    echo   Run: mimir deploy set SA_PASSWORD NewPassword
    exit /b 1
)

:: Get lowercase project name for Docker container naming
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set "CONTAINER=%COMPOSE_PROJECT_NAME%-sqlserver-1"

echo Changing sa password in container: %CONTAINER%
docker exec "%CONTAINER%" sqlcmd -S ".\SQLEXPRESS" -U sa -P "%OLD_PASSWORD%" -C -Q "ALTER LOGIN sa WITH PASSWORD = '%NEW_PASSWORD%'"
if errorlevel 1 (
    echo ERROR: Failed to change sa password. Is the old password correct?
    echo   Check the container is running: docker ps
    exit /b 1
)
echo SA password updated in %CONTAINER%.
echo Restart game servers to pick up the new password:
echo   mimir deploy restart-game
