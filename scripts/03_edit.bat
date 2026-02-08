@echo off
REM ============================================================
REM  Edit example: modify data via SQL and save back to JSON
REM  This uses the single-shot edit command.
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project

echo === Mimir Edit (single-shot) ===
echo.

echo --- Before: check LeatherBoots ---
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv, BuyPrice FROM ItemInfo WHERE InxName = 'LeatherBoots'"

echo.
echo --- Editing: set AC to 999 for LeatherBoots ---
%MIMIR% edit "%PROJECT%" "UPDATE ItemInfo SET AC = 999 WHERE InxName = 'LeatherBoots'"

echo.
echo --- After: verify change persisted to JSON ---
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, AC, DemandLv, BuyPrice FROM ItemInfo WHERE InxName = 'LeatherBoots'"

echo.
pause
