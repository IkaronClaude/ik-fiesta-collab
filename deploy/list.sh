#!/bin/bash
# List all deploy config variables for the project.
# Usage: mimir deploy list
source "$(dirname "$0")/common.sh"

ENV_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.env"
if [ ! -f "${ENV_FILE}" ]; then
    echo "No deploy config found for ${PROJECT} (${ENV_FILE})."
    echo "  Set a variable with: mimir deploy set KEY VALUE"
    exit 0
fi
echo "Deploy config for ${PROJECT}:"
echo "  (${ENV_FILE})"
echo ""
cat "${ENV_FILE}"
