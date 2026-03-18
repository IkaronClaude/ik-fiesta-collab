#!/bin/bash
# mimir - Linux equivalent of mimir.bat
# Forwards CLI commands to dotnet run, dispatches deploy/* scripts.
#
# Usage:
#   ./mimir.sh import
#   ./mimir.sh build --env server
#   ./mimir.sh deploy start
#   ./mimir.sh deploy server

set -eo pipefail

MIMIR_ROOT="$(cd "$(dirname "$0")" && pwd)"

if [ "${1:-}" != "deploy" ]; then
    # Forward to dotnet CLI
    dotnet run --project "${MIMIR_ROOT}/src/Mimir.Cli" -- "$@"
    exit $?
fi

# --- Deploy command ---
shift
DEPLOY_CMD="${1:-}"
if [ -z "${DEPLOY_CMD}" ]; then
    echo "Usage: mimir deploy <script>"
    echo "Available: server, update, restart-game, start, stop, logs, tail, rebuild-game, rebuild-sql, wipe-sql, reimport, set, get, clear, list, get-connection-string, set-sql-password, secret"
    exit 1
fi
shift

DEPLOY_SCRIPT="${MIMIR_ROOT}/deploy/${DEPLOY_CMD}.sh"
if [ ! -f "${DEPLOY_SCRIPT}" ]; then
    echo "ERROR: No deploy script found: ${DEPLOY_SCRIPT}"
    exit 1
fi

# Walk up from CWD to find the nearest mimir.json
MIMIR_PROJ_DIR="$(pwd)"
while true; do
    if [ -f "${MIMIR_PROJ_DIR}/mimir.json" ]; then
        break
    fi
    parent="$(dirname "${MIMIR_PROJ_DIR}")"
    if [ "${parent}" = "${MIMIR_PROJ_DIR}" ]; then
        echo "ERROR: mimir.json not found. Run 'mimir deploy' from inside a Mimir project directory."
        exit 1
    fi
    MIMIR_PROJ_DIR="${parent}"
done

PROJECT="$(basename "${MIMIR_PROJ_DIR}")"
export MIMIR_PROJ_DIR PROJECT

# Load per-project deploy config + secrets (existing env vars take precedence)
load_env_file() {
    local file="$1"
    [ -f "$file" ] || return 0
    while IFS='=' read -r key value; do
        [[ -z "$key" || "$key" =~ ^# ]] && continue
        key="$(echo "$key" | xargs)"
        if [ -z "${!key+x}" ]; then
            export "${key}=${value}"
        fi
    done < "$file"
}
load_env_file "${MIMIR_PROJ_DIR}/.mimir-deploy.env"
load_env_file "${MIMIR_PROJ_DIR}/.mimir-deploy.secrets"

# PORT_OFFSET
if [ -n "${PORT_OFFSET:-}" ]; then
    : "${LOGIN_PORT:=$((9010 + PORT_OFFSET))}"
    : "${WM_PORT:=$((9013 + PORT_OFFSET))}"
    : "${ZONE00_PORT:=$((9016 + PORT_OFFSET))}"
    : "${ZONE01_PORT:=$((9019 + PORT_OFFSET))}"
    : "${ZONE02_PORT:=$((9022 + PORT_OFFSET))}"
    : "${ZONE03_PORT:=$((9025 + PORT_OFFSET))}"
    : "${ZONE04_PORT:=$((9028 + PORT_OFFSET))}"
    : "${PATCH_PORT:=$((8080 + PORT_OFFSET))}"
    : "${API_PORT:=$((5000 + PORT_OFFSET))}"
    : "${WEBAPP_PORT:=$((80 + PORT_OFFSET))}"
    export LOGIN_PORT WM_PORT ZONE00_PORT ZONE01_PORT ZONE02_PORT
    export ZONE03_PORT ZONE04_PORT PATCH_PORT API_PORT WEBAPP_PORT
fi

bash "${DEPLOY_SCRIPT}" "${PROJECT}" "$@"
