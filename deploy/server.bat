@echo off
setlocal
:: Full deploy cycle: stop containers => mimir build => mimir pack => start containers
:: Run this after editing data or after a reimport to push changes to the running server
:: and make the patch server serve fresh client patches.
::
:: Usage: mimir deploy server          (from inside the project dir)
::        server.bat <project-name>    (direct call with explicit project name)
if "%~1"=="" (
    echo ERROR: Project name required.
    echo   Run via: mimir deploy server
    echo   Or:      server.bat ^<project-name^>
    exit /b 1
)
set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"
if not defined COMPOSE_PROJECT_NAME for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
cd /d "%~dp0"

echo === Stopping containers ===
docker compose --profile patch -f docker-compose.yml down

echo.
echo === Building Mimir project [%PROJECT%] ===
cd /d "%MIMIR_PROJ_DIR%"
call mimir build --all
if errorlevel 1 (
    echo ERROR: mimir build failed.
    pause
    exit /b 1
)

echo.
echo === Generating client patches ===
call mimir pack patches --env client
if errorlevel 1 (
    echo ERROR: mimir pack failed.
    pause
    exit /b 1
)

echo.
echo === Copying build to deployed snapshot ===
cd /d "%~dp0"
robocopy "%MIMIR_PROJ_DIR%\build\server" "%MIMIR_PROJ_DIR%\deployed\server" /E /PURGE /NFL /NDL /NJH /NJS
if errorlevel 8 (
    echo ERROR: robocopy failed.
    pause
    exit /b 1
)

echo.
echo === Ensuring required directories exist ===
if not exist "%MIMIR_PROJ_DIR%\patches" mkdir "%MIMIR_PROJ_DIR%\patches"
if not exist "%MIMIR_PROJ_DIR%\deployed\server" mkdir "%MIMIR_PROJ_DIR%\deployed\server"

echo.
echo === Starting containers ===
set DOCKER_BUILDKIT=0
docker compose --profile patch -f docker-compose.yml up -d

echo.
echo === Done ===
echo  Game server:  running
echo  Patch server: http://localhost:8080
echo  Run player\patch.bat to update your client before launching the game.
