@echo off
:: Re-import all server and client data into test-project, then rebuild.
:: WARNING: Wipes existing test-project data/ and build/ directories first.
cd /d "%~dp0.."
set /p CONFIRM="This will wipe test-project/data/ and test-project/build/. Continue? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Cancelled.
    pause
    exit /b 0
)
call mimir.bat init-template test-project
if errorlevel 1 (
    echo Template generation failed.
    pause
    exit /b 1
)
call mimir.bat import test-project --reimport
if errorlevel 1 (
    echo Import failed.
    pause
    exit /b 1
)
call mimir.bat build test-project ./test-project/build --all
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)
echo Done. Restart game servers with deploy\restart-game.bat
pause
