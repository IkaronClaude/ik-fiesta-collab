#!/bin/bash
# Tail logs from all game server containers.
# Usage: mimir deploy logs
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"
docker compose -f "${COMPOSE_FILE}" logs -f account accountlog character gamelog login worldmanager zone00 zone01 zone02 zone03 zone04
