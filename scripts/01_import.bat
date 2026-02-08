@echo off
REM ============================================================
REM  Import 9Data server + client files into a Mimir project
REM  Server: Z:\Odin Server Files\Server\9Data
REM  Client: Z:\Client\Fiesta Online\ressystem  (optional)
REM  Output: .\test-project
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set SERVER=Z:\Odin Server Files\Server\9Data
set CLIENT=Z:\Client\Fiesta Online\ressystem
set PROJECT=..\test-project

echo === Mimir Import ===
echo Server: %SERVER%

if exist "%CLIENT%" (
    echo Client: %CLIENT%
    echo.
    %MIMIR% import "%SERVER%" "%PROJECT%" --client "%CLIENT%"
) else (
    echo Client: not found, importing server only
    echo.
    %MIMIR% import "%SERVER%" "%PROJECT%"
)

echo.
echo Done. Check %PROJECT%\mimir.json for the manifest.
pause
