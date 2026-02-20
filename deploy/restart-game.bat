@echo off
:: Restart game server containers after a `mimir build` data update.
:: Copies build/server -> deployed/server snapshot, then restarts containers.
:: Does NOT rebuild the image or touch SQL.
cd /d "%~dp0"

echo === Copying build to deployed snapshot ===
robocopy "..\test-project\build\server" "..\test-project\deployed\server" /E /PURGE /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo ERROR: robocopy failed.
    pause
    exit /b 1
)

echo === Restarting containers ===
docker compose -f docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
