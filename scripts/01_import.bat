@echo off
REM ============================================================
REM  Import 9Data server files into a Mimir project
REM  Source: Z:\Odin Server Files\Server\9Data
REM  Output: .\test-project
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set SOURCE=Z:\Odin Server Files\Server\9Data
set PROJECT=..\test-project

echo === Mimir Import ===
echo Source: %SOURCE%
echo Project: %PROJECT%
echo.

%MIMIR% import "%SOURCE%" "%PROJECT%"

echo.
echo Done. Check %PROJECT%\mimir.json for the manifest.
pause
