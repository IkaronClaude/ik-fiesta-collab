@echo off
REM ============================================================
REM  Interactive SQL shell - loads once, run many queries/edits
REM
REM  Commands:
REM    SELECT ...          Run a query
REM    UPDATE/INSERT/DELETE Modify data (marks session as dirty)
REM    .tables             List all loaded tables
REM    .schema TableName   Show column definitions for a table
REM    .save               Save all tables back to JSON
REM    .quit               Quit (prompts to save if dirty)
REM
REM  Example session:
REM    mimir> SELECT InxName, Name, AC FROM ItemInfo WHERE InxName = 'LeatherBoots'
REM    mimir> UPDATE ItemInfo SET AC = 500 WHERE InxName = 'LeatherBoots'
REM    mimir> SELECT InxName, Name, AC FROM ItemInfo WHERE InxName = 'LeatherBoots'
REM    mimir> .save
REM    mimir> .quit
REM ============================================================

set MIMIR=dotnet run --project ..\src\Mimir.Cli --
set PROJECT=..\test-project

echo === Mimir Interactive Shell ===
echo.

%MIMIR% shell "%PROJECT%"
