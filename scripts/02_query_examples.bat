@echo off
REM ============================================================
REM  Example read-only queries against the Mimir project
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project

echo === Mimir Query Examples ===
echo.

echo --- Item count ---
%MIMIR% query "%PROJECT%" "SELECT COUNT(*) as total_items FROM ItemInfo"
echo.

echo --- Top 10 weapons by min damage ---
%MIMIR% query "%PROJECT%" "SELECT InxName, Name, MinWC, MaxWC, DemandLv FROM ItemInfo WHERE MinWC > 0 ORDER BY MinWC DESC LIMIT 10"
echo.

echo --- Mob count ---
%MIMIR% query "%PROJECT%" "SELECT COUNT(*) as total_mobs FROM MobInfo"
echo.

echo --- NPC shop: what does AdlAertsina sell? ---
%MIMIR% query "%PROJECT%" "SELECT * FROM AdlAertsina_Tab00"
echo.

echo --- Cross-reference: NPC shop items joined with ItemInfo ---
%MIMIR% query "%PROJECT%" "SELECT t.Column00 as ShopItem, i.Name, i.DemandLv, i.BuyPrice FROM AdlAertsina_Tab00 t JOIN ItemInfo i ON t.Column00 = i.InxName WHERE t.Column00 != '-'"
echo.

echo --- All tables by format ---
%MIMIR% query "%PROJECT%" "SELECT COUNT(*) as cnt FROM sqlite_master WHERE type='table'"
echo.

pause
