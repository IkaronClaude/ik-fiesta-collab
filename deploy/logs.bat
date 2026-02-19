@echo off
:: Tail logs from all game server containers.
cd /d "%~dp0"
docker compose -f docker-compose.yml --profile sql logs -f account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
