@echo off
setlocal
:: ================================================================
:: mimir deploy get KEY
:: Gets a single deploy config variable for the current project.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy get KEY
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy get KEY
    echo Example: mimir deploy get SA_PASSWORD
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "CFG_KEY=%~2"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if not exist "%ENV_FILE%" (
    echo ^(not set^)
    exit /b 0
)
for /f "usebackq tokens=1* delims==" %%K in ("%ENV_FILE%") do (
    if /i "%%K"=="%CFG_KEY%" (
        echo(%%L
        exit /b 0
    )
)
echo ^(not set^)
