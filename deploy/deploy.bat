@echo off
:: Full deploy cycle: stop containers => mimir build => mimir pack => start containers
:: Run this after editing data or after a reimport to push changes to the running server
:: and make the patch server serve fresh client patches.
cd /d "%~dp0"

echo === Stopping containers ===
docker compose --profile patch -f docker-compose.yml down

echo.
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
echo === Starting containers ===
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
docker compose --profile patch -f docker-compose.yml up -d

echo.
echo === Done ===
echo  Game server:  running
echo  Patch server: http://localhost:8080
echo  Run patcher\patch.bat to update your client before launching the game.
echo.
pause
