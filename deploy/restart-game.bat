@echo off
:: Restart game server containers after a `mimir build` data update.
:: Copies build/server -> deployed/server snapshot, then restarts containers.
:: Does NOT rebuild the image or touch SQL.
::
:: Usage: mimir deploy restart          (from inside the project dir)
::        restart-game.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy restart
    echo   Or:      restart-game.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
set COMPOSE_PROJECT_NAME=%PROJECT%
set PROJECT_NAME=%PROJECT%
cd /d "%~dp0"

echo === Copying build to deployed snapshot ===
robocopy "..\%PROJECT%\build\server" "..\%PROJECT%\deployed\server" /E /PURGE /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo ERROR: robocopy failed.
    pause
    exit /b 1
)

echo === Restarting containers ===
docker compose -f docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
