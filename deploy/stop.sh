#!/bin/bash
# Stop all containers. Data is preserved in the sql-data volume.
# Usage: mimir deploy stop
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"
docker compose --profile patch -f "${COMPOSE_FILE}" down
