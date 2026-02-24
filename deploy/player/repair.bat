@echo off
:: ================================================================
:: Mimir Client Repair  --  re-downloads all client files.
:: Use this if your client is broken, corrupted, or too old to patch.
:: Requires patch.bat in the same folder.
:: ================================================================
if not exist "%~dp0patch.bat" (
    echo ERROR: patch.bat not found in this folder.
    pause
    exit /b 1
)

set "VERSION_FILE=%~dp0.mimir-version"
echo -1 > "%VERSION_FILE%"
echo Repair mode set. Forcing full client re-download...
echo.
call "%~dp0patch.bat"
