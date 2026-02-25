@echo off
setlocal
:: ================================================================
:: mimir deploy set-sql-password NEW_PASSWORD
:: Sets the sa password and saves it to .mimir-deploy.env.
:: If SA_PASSWORD is already set in .mimir-deploy.env, it is used
:: to authenticate an ALTER LOGIN in the running SQL container.
:: If no old password exists (first run), only saves to env file.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy set-sql-password NEW_PASSWORD
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy set-sql-password NEW_PASSWORD
    echo   Sets the sa password and saves it to .mimir-deploy.env.
    echo   If SA_PASSWORD is already in .mimir-deploy.env, applies the change
    echo   to the running SQL container via ALTER LOGIN.
    echo   If no old password exists ^(first run^), only saves to env file.
    echo   Run 'mimir deploy restart-game' afterwards to pick up the new password.
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "NEW_PASSWORD=%~2"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"

:: Read current SA_PASSWORD (used as the old password to authenticate the change)
set "OLD_PASSWORD="
if exist "%ENV_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%ENV_FILE%") do (
        if /i "%%K"=="SA_PASSWORD" set "OLD_PASSWORD=%%L"
    )
)

:: Get lowercase project name for Docker container naming
for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set "CONTAINER=%COMPOSE_PROJECT_NAME%-sqlserver-1"

if "%OLD_PASSWORD%"=="" (
    echo No existing SA_PASSWORD found - saving to deploy config only.
    echo If the SQL container is already running with a different password,
    echo re-run with the current password already set in .mimir-deploy.env.
) else (
    echo Changing sa password in container: %CONTAINER%
    docker exec "%CONTAINER%" sqlcmd -S ".\SQLEXPRESS" -U sa -P "%OLD_PASSWORD%" -C -Q "ALTER LOGIN sa WITH PASSWORD = '%NEW_PASSWORD%'"
    if errorlevel 1 (
        echo ERROR: Failed to change sa password. Is the SQL container running?
        echo   Check: docker ps
        exit /b 1
    )
    echo SA password updated in %CONTAINER%.
)

:: Save new password to env file
set "TMP_FILE=%TEMP%\mimir-deploy-set-tmp.txt"
if exist "%ENV_FILE%" (
    findstr /v /b /c:"SA_PASSWORD=" "%ENV_FILE%" > "%TMP_FILE%" 2>nul
) else (
    type nul > "%TMP_FILE%"
)
echo SA_PASSWORD=%NEW_PASSWORD%>>"%TMP_FILE%"
move /y "%TMP_FILE%" "%ENV_FILE%" > nul

echo SA_PASSWORD saved to deploy config.
echo Run 'mimir deploy restart-game' to apply the new password to game servers.
