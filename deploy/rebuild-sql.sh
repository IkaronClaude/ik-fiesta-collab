#!/bin/bash
# Rebuild the SQL image and restart the SQL container.
# Database data is PRESERVED (stored in the sql-data volume).
# Use wipe-sql to destroy all data and restore from .bak files.
# Usage: mimir deploy rebuild-sql
source "$(dirname "$0")/common.sh"
cd "${SCRIPT_DIR}"

docker compose -f "${COMPOSE_FILE}" build sqlserver
docker compose -f "${COMPOSE_FILE}" up -d sqlserver

echo "SQL Server container rebuilt. SA_PASSWORD from deploy config will be applied on startup."
echo "If you need to change the password, run: mimir deploy set-sql-password NEW_PASSWORD"
