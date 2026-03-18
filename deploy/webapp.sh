#!/bin/bash
# Build and start the webapp container.
# Usage: mimir deploy webapp
source "$(dirname "$0")/common.sh"

: "${CERT_DIR:=${MIMIR_PROJ_DIR}/certs}"
export CERT_DIR
mkdir -p "${CERT_DIR}"

cd "${SCRIPT_DIR}"

# If WEBAPP_CONTEXT points to a user-supplied container, skip publishing StaticServer.
if [ -z "${WEBAPP_CONTEXT:-}" ]; then
    dotnet publish "${SCRIPT_DIR}/../src/Mimir.StaticServer" -c Release -o "${SCRIPT_DIR}/webapp-publish" --no-self-contained \
        || { echo "ERROR: dotnet publish failed."; exit 1; }
fi

docker compose -f "${COMPOSE_FILE}" --profile webapp build webapp
docker compose -f "${COMPOSE_FILE}" --profile webapp up -d webapp
