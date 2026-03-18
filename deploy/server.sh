#!/bin/bash
# Full deploy cycle: stop containers -> mimir build -> mimir pack -> start containers.
# Usage: mimir deploy server
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"

echo "=== Stopping game containers ==="
docker compose --profile patch -f "${COMPOSE_FILE}" stop \
    login worldmanager zone00 zone01 zone02 zone03 zone04 \
    account accountlog character gamelog patch-server

echo ""
echo "=== Building Mimir project [${PROJECT}] ==="
cd "${MIMIR_PROJ_DIR}"
bash "${SCRIPT_DIR}/../mimir.sh" build --all || { echo "ERROR: mimir build failed."; exit 1; }

echo ""
echo "=== Generating client patches ==="
bash "${SCRIPT_DIR}/../mimir.sh" pack patches --env client || { echo "ERROR: mimir pack failed."; exit 1; }

echo ""
echo "=== Copying build to deployed snapshot ==="
rsync -a --delete "${MIMIR_PROJ_DIR}/build/server/" "${MIMIR_PROJ_DIR}/deployed/server/"

echo ""
echo "=== Ensuring required directories exist ==="
mkdir -p "${MIMIR_PROJ_DIR}/patches" "${MIMIR_PROJ_DIR}/deployed/server"

echo ""
echo "=== Starting game containers ==="
cd "${SCRIPT_DIR}"
docker compose --profile patch -f "${COMPOSE_FILE}" up -d --force-recreate \
    login worldmanager zone00 zone01 zone02 zone03 zone04 \
    account accountlog character gamelog patch-server

echo ""
echo "=== Done ==="
echo "  Game server:  running"
echo "  Patch server: http://localhost:${PATCH_PORT:-8080}"
