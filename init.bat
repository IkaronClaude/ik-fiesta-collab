@echo off
setlocal enabledelayedexpansion
:: ============================================================
:: Mimir New Project Setup
:: Run this from the Mimir directory to bootstrap a new project.
:: ============================================================

echo.
echo  Mimir New Project Setup
echo  =======================
echo.

:: --- Locate mimir CLI ---
:: Prefer a published/installed exe on PATH; fall back to dotnet run.
set "MIMIR_CMD=mimir"
where mimir >nul 2>&1
if errorlevel 1 (
    echo  [info] 'mimir' not found on PATH. Will use: dotnet run --project "%~dp0src/Mimir.Cli"
    set "MIMIR_CMD=dotnet run --project "%~dp0src/Mimir.Cli" --"
)

:: --- Project directory ---
set /p "PROJ_DIR=Project directory (e.g. C:\Projects\MyServer): "
if "!PROJ_DIR!"=="" ( echo ERROR: No project directory specified. & exit /b 1 )
:: Strip trailing backslash
if "!PROJ_DIR:~-1!"=="\" set "PROJ_DIR=!PROJ_DIR:~0,-1!"

if exist "!PROJ_DIR!\mimir.json" (
    echo  [info] mimir.json already exists at !PROJ_DIR! - skipping mimir init.
    goto :env_setup
)

echo.
echo  Creating project at !PROJ_DIR! ...
%MIMIR_CMD% init "!PROJ_DIR!"
if errorlevel 1 ( echo ERROR: mimir init failed. & exit /b 1 )

:env_setup
echo.
echo  --- Environment Setup ---
echo.

:: --- Server ---
set /p "SERVER_PATH=Server data path (e.g. Z:\Server, or Enter to skip): "
if not "!SERVER_PATH!"=="" (
    cd /d "!PROJ_DIR!"
    %MIMIR_CMD% env server init "!SERVER_PATH!" --type server
    if errorlevel 1 ( echo WARNING: env server init failed - check the path and retry. )
    cd /d "%~dp0"

    :: Create deploy/server-files symlink so Docker can COPY binaries at build time
    if not exist "!PROJ_DIR!\deploy\server-files" (
        echo  Creating deploy\server-files symlink -^> !SERVER_PATH!
        mklink /D "!PROJ_DIR!\deploy\server-files" "!SERVER_PATH!" >nul
        if errorlevel 1 (
            echo  WARNING: Could not create symlink. You may need to run as Administrator,
            echo  or enable Developer Mode in Windows Settings.
            echo  Create it manually: mklink /D "!PROJ_DIR!\deploy\server-files" "!SERVER_PATH!"
        )
    ) else (
        echo  [info] deploy\server-files already exists - skipping symlink.
    )
)

:: --- Client ---
set /p "CLIENT_PATH=Client source path (e.g. Z:/ClientSource/ressystem, or Enter to skip): "
if not "!CLIENT_PATH!"=="" (
    cd /d "!PROJ_DIR!"
    %MIMIR_CMD% env client init "!CLIENT_PATH!" --type client
    if errorlevel 1 ( echo WARNING: env client init failed - check the path and retry. )
    cd /d "%~dp0"
)

:: --- Secrets ---
echo.
echo  --- Deploy Secrets ---
echo  (These are stored in .mimir-deploy.secrets, which is gitignored.)
echo.

set /p "SA_PASSWORD=SA_PASSWORD (SQL Server admin password, or Enter to skip): "
if not "!SA_PASSWORD!"=="" (
    cd /d "!PROJ_DIR!"
    %MIMIR_CMD% deploy secret set SA_PASSWORD !SA_PASSWORD!
    cd /d "%~dp0"
)

set /p "JWT_SECRET=JWT_SECRET (API auth secret, or Enter to skip): "
if not "!JWT_SECRET!"=="" (
    cd /d "!PROJ_DIR!"
    %MIMIR_CMD% deploy secret set JWT_SECRET !JWT_SECRET!
    cd /d "%~dp0"
)

:: --- Next steps ---
echo.
echo  ============================================================
echo  Done! Project created at: !PROJ_DIR!
echo.
echo  Next steps:
echo    cd "!PROJ_DIR!"
echo    mimir init-template      (generate merge rules)
echo    mimir import             (import all data)
echo    mimir build --all        (build output files)
echo  ============================================================
echo.
pause
