@echo off
REM ============================================================
REM  Full round-trip demo:
REM    1. Query original data
REM    2. Edit via SQL (modify an item)
REM    3. Verify change in JSON (query the project again)
REM    4. Build back to SHN
REM    5. Re-import the built SHN to a fresh project
REM    6. Verify change survived the round-trip
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project
set BUILD_OUT=..\build-output
set REIMPORT=..\reimport-project

echo === Full Round-Trip Test ===
echo.

echo [1/6] Original value:
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo [2/6] Editing: AC = 1337
%MIMIR% edit "%PROJECT%" "UPDATE ItemInfo SET AC = 1337 WHERE InxName = 'LeatherBoots'"
echo.

echo [3/6] Verify in JSON project:
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo [4/6] Building SHN from JSON...
if exist "%BUILD_OUT%" rmdir /s /q "%BUILD_OUT%"
%MIMIR% build "%PROJECT%" "%BUILD_OUT%"
echo.

echo [5/6] Re-importing built SHN to fresh project...
if exist "%REIMPORT%" rmdir /s /q "%REIMPORT%"
%MIMIR% import "%BUILD_OUT%\Shine" "%REIMPORT%"
echo.

echo [6/6] Verify value survived round-trip:
%MIMIR% query "%REIMPORT%" "SELECT InxName, Name, AC, DemandLv FROM ItemInfo WHERE InxName = 'LeatherBoots'"
echo.

echo === Round-trip complete ===
pause
