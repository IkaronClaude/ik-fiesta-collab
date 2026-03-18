#!/bin/bash
# Tail container logs. Optionally specify a service name.
# Usage: mimir deploy tail [service]
source "$(dirname "$0")/common.sh"
shift  # consume project name
cd "${SCRIPT_DIR}"
docker compose -f "${COMPOSE_FILE}" logs -f "$@"
