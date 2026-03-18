#!/bin/bash
# Rebuild the game server image and start all containers.
# Does NOT rebuild the SQL image -- use rebuild-sql for that.
# Usage: mimir deploy rebuild-game
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"

docker compose --profile patch -f "${COMPOSE_FILE}" build \
    account accountlog character gamelog \
    login worldmanager zone00 zone01 zone02 zone03 zone04
docker compose --profile patch -f "${COMPOSE_FILE}" up -d \
    account accountlog character gamelog \
    login worldmanager zone00 zone01 zone02 zone03 zone04 patch-server
