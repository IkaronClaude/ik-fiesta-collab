@echo off
setlocal

if "%~1"=="" (
    echo ERROR: Project name required.
    echo Usage: ci.bat ^<project-name^>
    exit /b 1
)

set "PROJECT=%~1"
if not defined MIMIR_PROJ_DIR set "MIMIR_PROJ_DIR=%~dp0..\%PROJECT%"

set "MIMIR_ENV_FILE=%MIMIR_PROJ_DIR%\.mimir-deploy.env"
if exist "%MIMIR_ENV_FILE%" (
    for /f "usebackq tokens=1* delims==" %%K in ("%MIMIR_ENV_FILE%") do set "%%K=%%L"
)

for /f "usebackq" %%L in (`powershell -NoProfile -Command "'%PROJECT%'.ToLower()"`) do set "COMPOSE_PROJECT_NAME=%%L"
set PROJECT_NAME=%PROJECT%
set DOCKER_BUILDKIT=0
cd /d "%~dp0"

:: --- Find docker.exe and copy for baking into the CI image ---
if not exist "%~dp0docker-bin" mkdir "%~dp0docker-bin"

set "DOCKER_EXE="
if exist "C:\Program Files\Docker\Docker\resources\bin\docker.exe" (
    set "DOCKER_EXE=C:\Program Files\Docker\Docker\resources\bin\docker.exe"
) else if exist "C:\Program Files\Docker\docker.exe" (
    set "DOCKER_EXE=C:\Program Files\Docker\docker.exe"
)

if not "%DOCKER_EXE%"=="" (
    copy "%DOCKER_EXE%" "%~dp0docker-bin\docker.exe" >nul
    echo Copied docker.exe from %DOCKER_EXE%
) else (
    echo WARNING: docker.exe not found - container restart step will be skipped.
    echo If needed, manually copy docker.exe to: %~dp0docker-bin\docker.exe
    echo   Expected: C:\Program Files\Docker\Docker\resources\bin\docker.exe
)

:: --- Publish Mimir.Webhook ---
dotnet publish "%~dp0..\src\Mimir.Webhook" -c Release -o "%~dp0ci-publish" --no-self-contained
if errorlevel 1 ( echo ERROR: publish Mimir.Webhook failed. & pause & exit /b 1 )

:: --- Publish Mimir.Cli (baked into image — same version that manages the project) ---
dotnet publish "%~dp0..\src\Mimir.Cli" -c Release -o "%~dp0ci-cli-publish" --no-self-contained
if errorlevel 1 ( echo ERROR: publish Mimir.Cli failed. & pause & exit /b 1 )

docker compose -f docker-compose.yml --profile ci build ci
docker compose -f docker-compose.yml --profile ci up -d ci
