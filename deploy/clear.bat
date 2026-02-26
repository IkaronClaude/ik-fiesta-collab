@echo off
setlocal
:: ================================================================
:: mimir deploy clear KEY
:: Removes a deploy config variable from .mimir-deploy.env.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy clear KEY
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy clear KEY
    echo Example: mimir deploy clear SA_PASSWORD
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
set "CFG_KEY=%~2"

set "ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
set "TMP_FILE=%TEMP%\mimir-deploy-set-tmp.txt"

if not exist "%ENV_FILE%" (
    echo %CFG_KEY% not found in deploy config ^(file does not exist^).
    exit /b 0
)

findstr /v /b /c:"%CFG_KEY%=" "%ENV_FILE%" > "%TMP_FILE%" 2>nul
move /y "%TMP_FILE%" "%ENV_FILE%" > nul
echo Cleared %CFG_KEY% from %PROJECT% deploy config (%ENV_FILE%).
