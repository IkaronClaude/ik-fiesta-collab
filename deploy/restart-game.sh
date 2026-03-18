#!/bin/bash
# Restart game server containers after a data update.
# Copies build/server -> deployed/server, then restarts containers.
# Does NOT rebuild the image or touch SQL.
# Usage: mimir deploy restart-game
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"

echo "=== Copying build to deployed snapshot ==="
rsync -a --delete "${MIMIR_PROJ_DIR}/build/server/" "${MIMIR_PROJ_DIR}/deployed/server/"

echo "=== Starting/restarting game containers ==="
docker compose -f "${COMPOSE_FILE}" up -d --force-recreate \
    account accountlog character gamelog \
    login worldmanager zone00 zone01 zone02 zone03 zone04
