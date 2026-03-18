#!/bin/bash
# common.sh - Shared preamble for all deploy scripts (Linux).
# Sourced by each deploy/*.sh script.
#
# Provides:
#   SCRIPT_DIR    - absolute path to deploy/ directory
#   PROJECT       - project name (basename of MIMIR_PROJ_DIR)
#   MIMIR_PROJ_DIR - absolute path to the Mimir project directory
#   COMPOSE_FILE  - always docker-compose.linux.yml on Linux
#   COMPOSE_PROJECT_NAME - lowercase project name

set -eo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
COMPOSE_FILE="docker-compose.linux.yml"

# --- Require project name (passed by mimir wrapper or as $1) ---
if [ -z "${PROJECT:-}" ]; then
    PROJECT="${1:-}"
fi
if [ -z "${PROJECT}" ]; then
    echo "ERROR: Project name required."
    echo "  Run via: mimir deploy <command>"
    exit 1
fi

# --- Resolve MIMIR_PROJ_DIR ---
if [ -z "${MIMIR_PROJ_DIR:-}" ]; then
    MIMIR_PROJ_DIR="${SCRIPT_DIR}/../${PROJECT}"
fi

# --- Load per-project deploy config + secrets ---
# .mimir-deploy.env    - general config (committable)
# .mimir-deploy.secrets - secrets like SA_PASSWORD (gitignored)
# Existing env vars take precedence over file values.
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

# --- PORT_OFFSET ---
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

# --- Compose project name (lowercase) ---
export COMPOSE_PROJECT_NAME="${COMPOSE_PROJECT_NAME:-$(echo "$PROJECT" | tr '[:upper:]' '[:lower:]')}"
export PROJECT MIMIR_PROJ_DIR COMPOSE_FILE
