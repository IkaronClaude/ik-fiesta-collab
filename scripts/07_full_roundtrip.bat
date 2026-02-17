@echo off
REM ============================================================
REM  Full round-trip demo (multi-environment):
REM    1. Init template from env scan
REM    2. Set conflict strategy on ColorInfo
REM    3. Import from configured environments
REM    4. Query original data
REM    5. Edit via SQL (modify an item)
REM    6. Verify change in JSON
REM    7. Build server env to SHN
REM    8. Re-import built SHN to a fresh project
REM    9. Verify change survived the round-trip
REM
REM  Requires: test-project/mimir.json with environments configured
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project
set BUILD_OUT=..\test-project\build
set REIMPORT=..\reimport-project

echo === Full Round-Trip Test (Multi-Environment) ===
echo.

echo [1/9] Generating merge template from environment scan...
%MIMIR% init-template "%PROJECT%"
echo.

echo [2/9] Setting conflict strategy on ColorInfo...
%MIMIR% edit-template "%PROJECT%" --table ColorInfo --conflict-strategy split
echo.

echo [3/9] Importing from all environments...
%MIMIR% import "%PROJECT%"
echo.

echo [4/9] Original value:
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo [5/9] Editing: AC = 1337
%MIMIR% edit "%PROJECT%" "UPDATE ItemInfo SET AC = 1337 WHERE InxName = 'LeatherBoots'"
echo.

echo [6/9] Verify in JSON project:
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo [7/9] Building all environments...
if exist "%BUILD_OUT%" rmdir /s /q "%BUILD_OUT%"
%MIMIR% build "%PROJECT%" "%BUILD_OUT%" --all
echo.

echo [8/9] Re-importing built server SHN to fresh project...
if exist "%REIMPORT%" rmdir /s /q "%REIMPORT%"
REM Create a single-env mimir.json for the reimport project
mkdir "%REIMPORT%" 2>nul
echo {"version":2,"environments":{"server":{"importPath":"..\\test-project\\build\\server"}},"tables":{}} > "%REIMPORT%\mimir.json"
%MIMIR% init-template "%REIMPORT%"
%MIMIR% import "%REIMPORT%"
echo.

echo [9/9] Verify value survived round-trip:
%MIMIR% query "%REIMPORT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo === Round-trip complete ===
pause
