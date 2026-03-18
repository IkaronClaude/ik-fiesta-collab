#!/bin/bash
# Remove a deploy config variable.
# Usage: mimir deploy clear KEY
source "$(dirname "$0")/common.sh"
shift  # consume project name

CFG_KEY="${1:-}"
if [ -z "${CFG_KEY}" ]; then
    echo "Usage: mimir deploy clear KEY"
    echo "Example: mimir deploy clear SA_PASSWORD"
    exit 1
fi

ENV_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.env"
if [ ! -f "${ENV_FILE}" ]; then
    echo "${CFG_KEY} not found in deploy config (file does not exist)."
    exit 0
fi

TMP_FILE="$(mktemp)"
grep -v "^${CFG_KEY}=" "${ENV_FILE}" > "${TMP_FILE}" 2>/dev/null || true
mv "${TMP_FILE}" "${ENV_FILE}"
echo "Cleared ${CFG_KEY} from ${PROJECT} deploy config (${ENV_FILE})."
