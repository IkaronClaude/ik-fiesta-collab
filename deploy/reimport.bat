@echo off
setlocal
:: Re-import all server and client data into the project, then rebuild.
:: WARNING: Wipes existing data/ and build/ directories first.
:: NOTE: mimir.template.json is preserved — run init-template manually if you need to regenerate it.
::
:: Usage: mimir deploy reimport          (from inside the project dir)
::        reimport.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy reimport
    echo   Or:      reimport.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
cd /d "%MIMIR_PROJ_DIR%"
call mimir reimport
