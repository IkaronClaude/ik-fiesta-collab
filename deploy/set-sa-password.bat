@echo off
setlocal
:: ================================================================
:: mimir deploy set-sa-password NEW_PASSWORD
:: Changes the sa password in the running SQL container and saves
:: the new password to .mimir-deploy.env.
:: The current SA_PASSWORD in .mimir-deploy.env is used to
:: authenticate the change.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy set-sa-password NEW_PASSWORD
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy set-sa-password NEW_PASSWORD
    echo   Changes the sa password in the running SQL container.
    echo   Reads the current SA_PASSWORD from .mimir-deploy.env to authenticate,
    echo   then applies NEW_PASSWORD and saves it to .mimir-deploy.env.
    echo   Run 'mimir deploy restart-game' afterwards to pick up the new password.
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "NEW_PASSWORD=%~2"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if not exist "%ENV_FILE%" (
    echo ERROR: No deploy config found at %ENV_FILE%
    echo   Run: mimir deploy set SA_PASSWORD CurrentPassword  first.
    exit /b 1
)

:: Read current SA_PASSWORD (used as the old password to authenticate the change)
set "OLD_PASSWORD="
for /f "usebackq tokens=1* delims==" %%K in ("%ENV_FILE%") do (
    if /i "%%K"=="SA_PASSWORD" set "OLD_PASSWORD=%%L"
)

if "%OLD_PASSWORD%"=="" (
    echo ERROR: SA_PASSWORD not set in deploy config. Cannot authenticate.
    echo   Run: mimir deploy set SA_PASSWORD CurrentPassword  first.
    exit /b 1
)

:: Get lowercase project name for Docker container naming
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set "CONTAINER=%COMPOSE_PROJECT_NAME%-sqlserver-1"

echo Changing sa password in container: %CONTAINER%
docker exec "%CONTAINER%" sqlcmd -S ".\SQLEXPRESS" -U sa -P "%OLD_PASSWORD%" -C -Q "ALTER LOGIN sa WITH PASSWORD = '%NEW_PASSWORD%'"
if errorlevel 1 (
    echo ERROR: Failed to change sa password. Is the SQL container running?
    echo   Check: docker ps
    exit /b 1
)

:: Save new password to env file
set "TMP_FILE=%TEMP%\mimir-deploy-set-tmp.txt"
findstr /v /b /c:"SA_PASSWORD=" "%ENV_FILE%" > "%TMP_FILE%" 2>nul
echo SA_PASSWORD=%NEW_PASSWORD%>>"%TMP_FILE%"
move /y "%TMP_FILE%" "%ENV_FILE%" > nul

echo SA password updated in %CONTAINER% and saved to deploy config.
echo Run 'mimir deploy restart-game' to apply the new password to game servers.
