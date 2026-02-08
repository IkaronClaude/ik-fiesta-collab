@echo off
REM ============================================================
REM  Build: convert JSON project back to server files (SHN + txt)
REM  Output goes to .\build-output, preserving directory structure
REM
REM  Note: SHN write is fully implemented (byte-level reconstruction).
REM  Raw table (.txt) write is not yet implemented - those will be skipped.
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project
set OUTPUT=..\build-output

echo === Mimir Build ===
echo Project: %PROJECT%
echo Output:  %OUTPUT%
echo.

if exist "%OUTPUT%" (
    echo Cleaning previous build output...
    rmdir /s /q "%OUTPUT%"
)

%MIMIR% build "%PROJECT%" "%OUTPUT%"

echo.
echo --- Output structure ---
dir /s /b "%OUTPUT%\*.shn" 2>nul | find /c ".shn"
echo SHN files built.
echo.
echo Check %OUTPUT%\ for the reconstructed server files.
pause
