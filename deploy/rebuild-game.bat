@echo off
:: Start all containers (SQL + game servers). Builds game server image if needed.
:: SQL image is NOT rebuilt — use rebuild-sql.bat for that.
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
docker compose --profile patch -f docker-compose.yml build account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
docker compose --profile patch -f docker-compose.yml up -d
pause
