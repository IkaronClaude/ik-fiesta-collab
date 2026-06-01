@echo off
REM ============================================================
REM  Import from configured environments into a fiesta project.
REM  Reads environment paths from test-project/fiesta.json.
REM  Run init-template first if fiesta.template.json doesn't exist.
REM ============================================================

set MIMIR=dotnet run --project ..\src\Fiesta.Collab.Cli --
set PROJECT=..\test-project

echo === Mimir Import ===
echo Project: %PROJECT%
echo.

REM Generate template if it doesn't exist
if not exist "%PROJECT%\fiesta.template.json" (
    echo Generating template...
    %MIMIR% init-template "%PROJECT%"
    echo.
)

%MIMIR% import "%PROJECT%"

echo.
echo Done. Check %PROJECT%\fiesta.json for the manifest.
pause
