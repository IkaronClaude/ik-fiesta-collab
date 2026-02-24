@echo off
:: Quick data update cycle: build -> pack -> snapshot -> restart game servers.
:: Does NOT stop/start SQL or rebuild Docker images.
:: Use this for iterative data changes. Use deploy.bat for first-time or full deploys.
::
:: Usage: mimir deploy update          (from inside the project dir)
::        update.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy update
    echo   Or:      update.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
cd /d "%~dp0"

echo === Building Mimir project [%PROJECT%] ===
cd /d "%~dp0..\%PROJECT%"
mimir build --all
if errorlevel 1 (
    echo ERROR: mimir build failed.
    pause
    exit /b 1
)

echo.
echo === Generating client patches ===
mimir pack patches --env client
if errorlevel 1 (
    echo ERROR: mimir pack failed.
    pause
    exit /b 1
)

echo.
echo === Copying build to deployed snapshot ===
cd /d "%~dp0"
robocopy "..\%PROJECT%\build\server" "..\%PROJECT%\deployed\server" /E /PURGE /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo ERROR: robocopy failed.
    pause
    exit /b 1
)

echo.
echo === Restarting game server containers ===
set COMPOSE_PROJECT_NAME=%PROJECT%
set PROJECT_NAME=%PROJECT%
docker compose -f docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04

echo.
echo === Done ===
echo  Server data updated and restarted.
echo  Patch server: http://localhost:8080
echo  Run player\patch.bat to update your client before launching the game.
echo.
pause
