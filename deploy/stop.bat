@echo off
:: Stop all containers. Data is preserved in the sql-data volume.
cd /d "%~dp0"
docker compose --profile patch -f docker-compose.yml down
pause
