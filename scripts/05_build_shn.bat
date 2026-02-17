@echo off
REM ============================================================
REM  Build: convert JSON project back to server/client files
REM  Output goes to test-project\build\{env}\, preserving
REM  original directory structure per environment.
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project
set OUTPUT=..\test-project\build

echo === Mimir Build (All Environments) ===
echo Project: %PROJECT%
echo Output:  %OUTPUT%
echo.

if exist "%OUTPUT%" (
    echo Cleaning previous build output...
    rmdir /s /q "%OUTPUT%"
)

%MIMIR% build "%PROJECT%" "%OUTPUT%" --all

echo.
echo --- Server output ---
dir /s /b "%OUTPUT%\server\*.shn" 2>nul | find /c ".shn"
echo SHN files built for server.
echo.
echo --- Client output ---
dir /s /b "%OUTPUT%\client\*.shn" 2>nul | find /c ".shn"
echo SHN files built for client.
echo.
echo Check %OUTPUT%\ for the reconstructed files.
pause
