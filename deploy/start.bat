@echo off
:: Start all containers without rebuilding anything.
:: Use rebuild-game.bat if you've changed server binaries or scripts.
cd /d "%~dp0"
set DOCKER_BUILDKIT=0
docker compose --profile patch -f docker-compose.yml up -d
pause
