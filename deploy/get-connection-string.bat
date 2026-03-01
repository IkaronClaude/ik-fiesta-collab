@echo off
setlocal
:: ================================================================
:: mimir deploy get-connection-string [account|character|all]
:: Prints the SQL connection strings using the current deploy config.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy get-connection-string
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "FILTER=%~2"
if "%FILTER%"=="" set "FILTER=all"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
set "SECRETS_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.secrets"
set "SA_PASSWORD="
set "WORLD_DB_NAME=World00_Character"
set "SQL_PORT=1433"
for %%F in ("%ENV_FILE%" "%SECRETS_FILE%") do (
    if exist "%%~F" (
        for /f "usebackq tokens=1* delims==" %%K in ("%%~F") do (
            if "%%K"=="SA_PASSWORD"   set "SA_PASSWORD=%%L"
            if "%%K"=="WORLD_DB_NAME" set "WORLD_DB_NAME=%%L"
            if "%%K"=="SQL_PORT"      set "SQL_PORT=%%L"
        )
    )
)
if "%SA_PASSWORD%"=="" (
    echo ERROR: SA_PASSWORD not set. Run: mimir deploy secret set SA_PASSWORD YourStrongPassword1
    exit /b 1
)

if /i not "%FILTER%"=="character" (
    echo Account:
    echo   Server=localhost,%SQL_PORT%\SQLEXPRESS;Database=Account;User Id=sa;Password=%SA_PASSWORD%;TrustServerCertificate=True;
    echo.
)
if /i not "%FILTER%"=="account" (
    echo Character ^(%WORLD_DB_NAME%^):
    echo   Server=localhost,%SQL_PORT%\SQLEXPRESS;Database=%WORLD_DB_NAME%;User Id=sa;Password=%SA_PASSWORD%;TrustServerCertificate=True;
    echo.
)
