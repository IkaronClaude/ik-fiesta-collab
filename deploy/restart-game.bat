@echo off
:: Restart game server containers after a `mimir build` data update.
:: Does NOT rebuild the image or touch SQL.
cd /d "%~dp0"
docker compose -f docker-compose.yml restart account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
pause
