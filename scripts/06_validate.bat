@echo off
REM ============================================================
REM  Validate project against constraint rules
REM  Checks FK references defined in mimir.constraints.json
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project

echo === Mimir Validate ===
echo.

%MIMIR% validate "%PROJECT%"

echo.
pause
