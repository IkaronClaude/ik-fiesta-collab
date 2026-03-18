#!/bin/bash
# Re-import all server and client data into the project, then rebuild.
# WARNING: Wipes existing data/ and build/ directories first.
# Usage: mimir deploy reimport
source "$(dirname "$0")/common.sh"
cd "${MIMIR_PROJ_DIR}"
bash "${SCRIPT_DIR}/../mimir.sh" reimport
