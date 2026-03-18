#!/bin/bash
# Start all containers without rebuilding.
# Usage: mimir deploy start
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"
docker compose --profile patch -f "${COMPOSE_FILE}" up -d
