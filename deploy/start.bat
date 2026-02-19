@echo off
:: Start all containers without rebuilding anything.
:: Use rebuild-game.bat if you've changed server binaries or scripts.
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
docker compose -f docker-compose.yml --profile sql up -d
pause
