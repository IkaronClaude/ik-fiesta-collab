@echo off
:: Rebuild the SQL image and wipe the database volume for a clean restore.
:: WARNING: This deletes all database data. Only use for initial setup or after .bak changes.
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
echo WARNING: This will delete all SQL data and restore from .bak files.
set /p CONFIRM="Are you sure? (y/N): "
if /i not "%CONFIRM%"=="y" (
    echo Cancelled.
    pause
    exit /b 0
)
docker compose -f docker-compose.yml --profile sql down -v
docker compose -f docker-compose.yml --profile sql build sqlserver
docker compose -f docker-compose.yml --profile sql up -d sqlserver
pause
