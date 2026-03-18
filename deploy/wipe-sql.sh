#!/bin/bash
# Wipe the SQL data volume and restore all databases from .bak files.
# WARNING: This DESTROYS all database data.
# Usage: mimir deploy wipe-sql
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"

echo "WARNING: This will DELETE ALL SQL DATA for project '${PROJECT}' and restore from .bak files."
read -rp "Are you sure? (y/N): " confirm
if [[ ! "${confirm}" =~ ^[Yy]$ ]]; then
    echo "Cancelled."
    exit 0
fi

docker compose -f "${COMPOSE_FILE}" down -v
docker compose -f "${COMPOSE_FILE}" build sqlserver
docker compose -f "${COMPOSE_FILE}" up -d sqlserver

echo "SQL data wiped and container rebuilt."
echo "Run 'mimir deploy set-sql-password NEW_PASSWORD' to change the password."
