@echo off
REM ============================================================
REM  Import from configured environments into a Mimir project.
REM  Reads environment paths from test-project/mimir.json.
REM  Run init-template first if mimir.template.json doesn't exist.
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project

echo === Mimir Import ===
echo Project: %PROJECT%
echo.

REM Generate template if it doesn't exist
if not exist "%PROJECT%\mimir.template.json" (
    echo Generating template...
    %MIMIR% init-template "%PROJECT%"
    echo.
)

%MIMIR% import "%PROJECT%"

echo.
echo Done. Check %PROJECT%\mimir.json for the manifest.
pause
