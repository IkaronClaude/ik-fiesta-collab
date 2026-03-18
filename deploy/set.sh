#!/bin/bash
# Set a deploy config variable.
# Usage: mimir deploy set KEY VALUE
source "$(dirname "$0")/common.sh"
shift  # consume project name

CFG_KEY="${1:-}"
CFG_VAL="${2:-}"
if [ -z "${CFG_KEY}" ] || [ -z "${CFG_VAL}" ]; then
    echo "Usage: mimir deploy set KEY VALUE"
    echo "Example: mimir deploy set SA_PASSWORD MyStrongPassword1"
    exit 1
fi

ENV_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.env"
TMP_FILE="$(mktemp)"

if [ -f "${ENV_FILE}" ]; then
    grep -v "^${CFG_KEY}=" "${ENV_FILE}" > "${TMP_FILE}" 2>/dev/null || true
fi
echo "${CFG_KEY}=${CFG_VAL}" >> "${TMP_FILE}"
mv "${TMP_FILE}" "${ENV_FILE}"

echo "Set ${CFG_KEY} in ${PROJECT} deploy config (${ENV_FILE})."
