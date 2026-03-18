#!/bin/bash
# Quick data update: build -> pack -> snapshot -> restart game servers.
# Does NOT stop/start SQL or rebuild Docker images.
# Usage: mimir deploy update
source "$(dirname "$0")/common.sh"

echo "=== Building Mimir project [${PROJECT}] ==="
cd "${MIMIR_PROJ_DIR}"
mimir build --all || { echo "ERROR: mimir build failed."; exit 1; }

echo ""
echo "=== Generating client patches ==="
mimir pack patches --env client || { echo "ERROR: mimir pack failed."; exit 1; }

echo ""
echo "=== Copying build to deployed snapshot ==="
rsync -a --delete "${MIMIR_PROJ_DIR}/build/server/" "${MIMIR_PROJ_DIR}/deployed/server/"

echo ""
echo "=== Restarting game server containers ==="
cd "${SCRIPT_DIR}"
docker compose -f "${COMPOSE_FILE}" restart \
    account accountlog character gamelog \
    login worldmanager zone00 zone01 zone02 zone03 zone04

echo ""
echo "=== Done ==="
echo "  Server data updated and restarted."
echo "  Patch server: http://localhost:${PATCH_PORT:-8080}"
