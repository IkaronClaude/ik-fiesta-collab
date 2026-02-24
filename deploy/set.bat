@echo off
setlocal
:: ================================================================
:: mimir deploy set KEY=VALUE
:: Writes a deploy config variable for the current project.
:: Variables are stored in <project>/.mimir-deploy.env and loaded
:: automatically by all deploy scripts.
:: ================================================================
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy set KEY=VALUE
    exit /b 1
)
if "%~2"=="" (
    echo Usage: mimir deploy set KEY=VALUE
    echo Example: mimir deploy set SA_PASSWORD=MyStrongPassword1
    exit /b 1
)

set "PROJECT=%~1"
for /f "tokens=1* delims==" %%K in ("%~2") do (
    set "CFG_KEY=%%K"
    set "CFG_VAL=%%L"
)
if "%CFG_KEY%"=="" (
    echo ERROR: Invalid format. Use KEY=VALUE.
    exit /b 1
)

set "ENV_FILE=%~dp0..\%PROJECT%\.mimir-deploy.env"
set "TMP_FILE=%TEMP%\mimir-deploy-set-tmp.txt"

if exist "%ENV_FILE%" (
    findstr /v /b /c:"%CFG_KEY%=" "%ENV_FILE%" > "%TMP_FILE%" 2>nul
) else (
    type nul > "%TMP_FILE%"
)
echo %CFG_KEY%=%CFG_VAL%>>"%TMP_FILE%"
move /y "%TMP_FILE%" "%ENV_FILE%" > nul
echo Set %CFG_KEY% in %PROJECT% deploy config (%ENV_FILE%).
