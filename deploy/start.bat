@echo off
:: Start all containers (SQL + game servers). Builds game server image if needed.
:: SQL image is NOT rebuilt — use rebuild-sql.bat for that.
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
docker compose -f docker-compose.yml build
docker compose -f docker-compose.yml --profile sql up -d
pause
