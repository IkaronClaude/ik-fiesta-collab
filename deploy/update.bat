@echo off
:: Quick data update cycle: build -> pack -> snapshot -> restart game servers.
:: Does NOT stop/start SQL or rebuild Docker images.
:: Use this for iterative data changes. Use deploy.bat for first-time or full deploys.
cd /d "%~dp0"

echo === Building Mimir project ===
cd /d "%~dp0..\test-project"
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
robocopy "..\test-project\build\server" "..\test-project\deployed\server" /E /PURGE /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo ERROR: robocopy failed.
    pause
    exit /b 1
)

echo.
echo === Restarting game server containers ===
docker compose -f docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04

echo.
echo === Done ===
echo  Server data updated and restarted.
echo  Patch server: http://localhost:8080
echo  Run patcher\patch.bat to update your client before launching the game.
echo.
pause
