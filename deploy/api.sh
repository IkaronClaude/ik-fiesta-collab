#!/bin/bash
# Build and start the API container.
# Usage: mimir deploy api
source "$(dirname "$0")/common.sh"

: "${CERT_DIR:=${MIMIR_PROJ_DIR}/certs}"
export CERT_DIR
mkdir -p "${CERT_DIR}"

cd "${SCRIPT_DIR}"

dotnet publish "${SCRIPT_DIR}/../src/Mimir.Api" -c Release -o "${SCRIPT_DIR}/api-publish" --no-self-contained \
    || { echo "ERROR: dotnet publish failed."; exit 1; }

docker compose -f "${COMPOSE_FILE}" --profile api build api
docker compose -f "${COMPOSE_FILE}" --profile api up -d api
