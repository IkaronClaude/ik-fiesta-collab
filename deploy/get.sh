#!/bin/bash
# Get a deploy config variable.
# Usage: mimir deploy get KEY
source "$(dirname "$0")/common.sh"
shift  # consume project name

CFG_KEY="${1:-}"
if [ -z "${CFG_KEY}" ]; then
    echo "Usage: mimir deploy get KEY"
    echo "Example: mimir deploy get SA_PASSWORD"
    exit 1
fi

ENV_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.env"
if [ -f "${ENV_FILE}" ]; then
    value="$(grep "^${CFG_KEY}=" "${ENV_FILE}" 2>/dev/null | head -1 | cut -d= -f2-)"
    if [ -n "${value}" ]; then
        echo "${value}"
        exit 0
    fi
fi
echo "(not set)"
