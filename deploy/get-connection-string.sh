#!/bin/bash
# Print SQL connection strings using the current deploy config.
# Usage: mimir deploy get-connection-string [account|character|all]
source "$(dirname "$0")/common.sh"
shift  # consume project name

FILTER="${1:-all}"

if [ -z "${SA_PASSWORD:-}" ]; then
    echo "ERROR: SA_PASSWORD not set. Run: mimir deploy secret set SA_PASSWORD YourStrongPassword1"
    exit 1
fi

WORLD_DB="${WORLD_DB_NAME:-World00_Character}"
SQL_PORT_VAL="${SQL_PORT:-1433}"

if [ "${FILTER}" != "character" ]; then
    echo "Account:"
    echo "  Server=localhost,${SQL_PORT_VAL};Database=Account;User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;"
    echo ""
fi
if [ "${FILTER}" != "account" ]; then
    echo "Character (${WORLD_DB}):"
    echo "  Server=localhost,${SQL_PORT_VAL};Database=${WORLD_DB};User Id=sa;Password=${SA_PASSWORD};TrustServerCertificate=True;"
    echo ""
fi
