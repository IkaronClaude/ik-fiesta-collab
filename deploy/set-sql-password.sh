#!/bin/bash
# Set the SA password and save it to .mimir-deploy.env.
# If SA_PASSWORD is already set, applies ALTER LOGIN to the running SQL container.
# Usage: mimir deploy set-sql-password NEW_PASSWORD
source "$(dirname "$0")/common.sh"
shift  # consume project name

NEW_PASSWORD="${1:-}"
if [ -z "${NEW_PASSWORD}" ]; then
    echo "Usage: mimir deploy set-sql-password NEW_PASSWORD"
    echo "  Sets the sa password and saves it to .mimir-deploy.env."
    echo "  Run 'mimir deploy rebuild-game' afterwards to pick up the new password."
    exit 1
fi

ENV_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.env"

# Read current password
OLD_PASSWORD=""
if [ -f "${ENV_FILE}" ]; then
    OLD_PASSWORD="$(grep "^SA_PASSWORD=" "${ENV_FILE}" 2>/dev/null | head -1 | cut -d= -f2-)"
fi

CONTAINER="${COMPOSE_PROJECT_NAME}-sqlserver-1"

if [ -z "${OLD_PASSWORD}" ]; then
    echo "No existing SA_PASSWORD found - saving to deploy config only."
else
    echo "Changing sa password in container: ${CONTAINER}"
    docker exec "${CONTAINER}" /opt/mssql-tools18/bin/sqlcmd \
        -S localhost -U sa -P "${OLD_PASSWORD}" -C \
        -Q "ALTER LOGIN sa WITH PASSWORD = '${NEW_PASSWORD}'" \
        || { echo "ERROR: Failed to change sa password. Is the SQL container running?"; exit 1; }
    echo "SA password updated in ${CONTAINER}."
fi

# Save new password to env file
TMP="$(mktemp)"
if [ -f "${ENV_FILE}" ]; then
    grep -v "^SA_PASSWORD=" "${ENV_FILE}" > "${TMP}" 2>/dev/null || true
fi
echo "SA_PASSWORD=${NEW_PASSWORD}" >> "${TMP}"
mv "${TMP}" "${ENV_FILE}"

echo "SA_PASSWORD saved to deploy config."
echo "Run 'mimir deploy rebuild-game' to apply the new password to game servers."
