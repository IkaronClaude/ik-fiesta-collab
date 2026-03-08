#!/bin/bash
# setup-sql.linux.sh - SQL Server Linux entrypoint
# Starts SQL Server, restores game databases from .bak files on first run,
# then keeps the process running via the SQL Server foreground process.

set -eo pipefail

SA_PASSWORD="${SA_PASSWORD:?SA_PASSWORD not set. Run: mimir deploy set SA_PASSWORD YourStrongPassword1}"
BACKUP_DIR="/var/opt/mssql/backup"
DATA_DIR="/var/opt/mssql/data"
SQLCMD="/opt/mssql-tools18/bin/sqlcmd"

# Older image versions ship tools at a different path
[ -x "${SQLCMD}" ] || SQLCMD="/opt/mssql-tools/bin/sqlcmd"

# Start SQL Server in the background, then restore databases, then hand off to foreground.
echo "Starting SQL Server..."
/opt/mssql/bin/sqlservr &
SQL_PID=$!

# Wait for SQL Server to accept connections
echo "Waiting for SQL Server to become ready..."
for i in $(seq 1 60); do
    if "${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -Q "SELECT 1" > /dev/null 2>&1; then
        echo "SQL Server is ready (${i}s)."
        break
    fi
    if [ "${i}" -eq 60 ]; then
        echo "ERROR: SQL Server did not become ready after 60s."
        kill "${SQL_PID}" 2>/dev/null
        exit 1
    fi
    sleep 1
done

# Enable remote TCP connections
"${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -Q \
    "EXEC sp_configure 'remote access', 1; RECONFIGURE;" > /dev/null 2>&1 || true

# Restore databases from .bak files (skip if already present on the volume)
DATABASES=("Account" "AccountLog" "OperatorTool" "Options" "StatisticsData" "World00_Character" "World00_GameLog")

for DB in "${DATABASES[@]}"; do
    BAK="${BACKUP_DIR}/${DB}.bak"

    if [ ! -f "${BAK}" ]; then
        echo "WARNING: Backup not found: ${BAK} — skipping."
        continue
    fi

    # Check if database already exists (persisted on the volume)
    DB_COUNT=$("${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -h -1 -W \
        -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'${DB}'" \
        2>/dev/null | tr -d '[:space:]')

    if [ "${DB_COUNT}" = "1" ]; then
        echo "Database '${DB}' already exists — skipping restore."
        continue
    fi

    # Check if data files exist but the DB is not registered (container recreation with existing volume)
    MDF="${DATA_DIR}/${DB}.mdf"
    if [ -f "${MDF}" ]; then
        echo "Data file found for '${DB}' — attaching..."
        LDF="${DATA_DIR}/${DB}_log.ldf"
        if [ -f "${LDF}" ]; then
            ATTACH_SQL="CREATE DATABASE [${DB}] ON (FILENAME = '${MDF}'), (FILENAME = '${LDF}') FOR ATTACH_REBUILD_LOG"
        else
            ATTACH_SQL="CREATE DATABASE [${DB}] ON (FILENAME = '${MDF}') FOR ATTACH_REBUILD_LOG"
        fi
        "${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -Q "${ATTACH_SQL}" 2>&1 \
            && echo "Database '${DB}' attached." \
            || echo "WARNING: Attach failed for '${DB}'."
        continue
    fi

    echo "Restoring database '${DB}' from ${BAK}..."

    # Build MOVE clause from FILELISTONLY
    MOVE_CLAUSE=""
    DATA_IDX=0
    LOG_IDX=0
    while IFS='|' read -r logical_name physical_name type _rest; do
        logical_name="${logical_name// /}"
        type="${type// /}"
        case "${type}" in
            D)
                SUFFIX=$( [ "${DATA_IDX}" -eq 0 ] && echo "" || echo "_${DATA_IDX}" )
                MOVE_CLAUSE+="MOVE '${logical_name}' TO '${DATA_DIR}/${DB}${SUFFIX}.mdf', "
                DATA_IDX=$((DATA_IDX + 1))
                ;;
            L)
                SUFFIX=$( [ "${LOG_IDX}" -eq 0 ] && echo "" || echo "_${LOG_IDX}" )
                MOVE_CLAUSE+="MOVE '${logical_name}' TO '${DATA_DIR}/${DB}${SUFFIX}_log.ldf', "
                LOG_IDX=$((LOG_IDX + 1))
                ;;
        esac
    done < <("${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -s "|" -h -1 -W \
        -Q "RESTORE FILELISTONLY FROM DISK = '${BAK}'" 2>/dev/null)

    MOVE_CLAUSE="${MOVE_CLAUSE%, }"  # strip trailing ", "

    if [ -n "${MOVE_CLAUSE}" ]; then
        RESTORE_SQL="RESTORE DATABASE [${DB}] FROM DISK = '${BAK}' WITH ${MOVE_CLAUSE}"
    else
        RESTORE_SQL="RESTORE DATABASE [${DB}] FROM DISK = '${BAK}'"
    fi

    "${SQLCMD}" -S localhost -U sa -P "${SA_PASSWORD}" -C -Q "${RESTORE_SQL}" 2>&1 \
        && echo "Database '${DB}' restored." \
        || echo "WARNING: Restore failed for '${DB}'."
done

echo "SQL Server setup complete."

# Hand off to the foreground SQL Server process
wait "${SQL_PID}"
