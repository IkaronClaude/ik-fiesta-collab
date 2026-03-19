#!/bin/bash
# Build and start the webapp container.
# Usage: mimir deploy webapp
source "$(dirname "$0")/common.sh"

: "${CERT_DIR:=${MIMIR_PROJ_DIR}/certs}"
export CERT_DIR
mkdir -p "${CERT_DIR}"

cd "${SCRIPT_DIR}"

# If WEBAPP_CONTEXT points to a user-supplied container, skip copying/publishing.
if [ -z "${WEBAPP_CONTEXT:-}" ]; then
    # Copy static SPA files (no .NET publish needed — nginx serves them directly)
    mkdir -p "${SCRIPT_DIR}/webapp-files"
    cp -a "${SCRIPT_DIR}/../src/Mimir.StaticServer/wwwroot/." "${SCRIPT_DIR}/webapp-files/"
    echo "Copied SPA files to webapp-files/"
fi

docker compose -f "${COMPOSE_FILE}" --profile webapp build webapp
docker compose -f "${COMPOSE_FILE}" --profile webapp up -d webapp
