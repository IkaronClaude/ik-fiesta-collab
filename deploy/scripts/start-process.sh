#!/bin/bash
# start-process.sh - Single game process entrypoint (Linux/Wine)
# Mirrors start-process.ps1 for the Linux+Wine container variant.
# Runs one Windows game server exe under Wine.

set -eo pipefail

PROCESS_NAME="${PROCESS_NAME:?PROCESS_NAME env var not set}"
PROCESS_EXE="${PROCESS_EXE:?PROCESS_EXE env var not set}"
KEEP_ALIVE="${KEEP_ALIVE:-0}"

PROCESS_DIR="/server/${PROCESS_NAME}"
EXE_PATH="${PROCESS_DIR}/${PROCESS_EXE}"

echo "=== Starting ${PROCESS_NAME} (${PROCESS_EXE}) under Wine ==="
echo "Process dir: ${PROCESS_DIR}"

# --- Copy process binaries from mounted volume to writable directory ---
# Server binaries are mounted read-only at /server-bins/ from DEPLOY_PATH.
# We copy to /server/ because the game exes write logs to their own directory.
BINS_DIR="/server-bins"
if [ -d "${BINS_DIR}/${PROCESS_NAME}" ]; then
    mkdir -p "${PROCESS_DIR}"
    cp -a "${BINS_DIR}/${PROCESS_NAME}/." "${PROCESS_DIR}/"
    echo "Copied binaries: ${BINS_DIR}/${PROCESS_NAME}/ -> ${PROCESS_DIR}/"
fi

# Copy GamigoZR for Zone processes (anti-cheat dependency)
if [[ "${PROCESS_NAME}" =~ ^Zone ]] && [ -d "${BINS_DIR}/GamigoZR" ]; then
    mkdir -p /server/GamigoZR
    cp -a "${BINS_DIR}/GamigoZR/." /server/GamigoZR/
fi

if [ ! -f "${EXE_PATH}" ]; then
    echo "ERROR: Executable not found: ${EXE_PATH}"
    echo "  Check that DEPLOY_PATH is set and contains ${PROCESS_NAME}/${PROCESS_EXE}"
    exit 1
fi

# Start a persistent Xvfb (Wine needs X11 even for headless server processes)
# Clean stale lock files from container restarts
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99 2>/dev/null || true
Xvfb :99 -screen 0 800x600x24 &
export DISPLAY=:99
sleep 1

# Kill any stale wineserver from a previous run (container restart).
# The Wine prefix from the Docker build is kept intact — stale service
# registrations are cleaned up by sc.exe delete in Step 6.
wineserver -k 2>/dev/null || true

# Wine maps Z:\ to / (Linux root). All paths use Z:\server\... so
# the exe can find its config files and DLLs at /server/ on disk.

# --- Step 1: Copy per-process ServerInfo config ---

copy_config() {
    if [ -f "$1" ]; then
        cp "$1" "$2"
        echo "Copied $1 -> $2"
    fi
}

case "${PROCESS_NAME}" in
    Account)      copy_config /docker-config/DataServerInfo_Account.txt    "${PROCESS_DIR}/DataServerInfo_Account.txt" ;;
    AccountLog)   copy_config /docker-config/DataServerInfo_AccountLog.txt "${PROCESS_DIR}/DataServerInfo_AccountLog.txt" ;;
    Character)    copy_config /docker-config/DataServerInfo_Character.txt  "${PROCESS_DIR}/DataServerInfo_Character.txt" ;;
    GameLog)      copy_config /docker-config/DataServerInfo_GameLog.txt    "${PROCESS_DIR}/DataServerInfo_GameLog.txt" ;;
    Login)        copy_config /docker-config/LoginServerInfo.txt           "${PROCESS_DIR}/LoginServerInfo.txt" ;;
    WorldManager) copy_config /docker-config/WMServerInfo.txt              "${PROCESS_DIR}/WMServerInfo.txt" ;;
    Zone*)
        ZONE_NUMBER="${ZONE_NUMBER:?ZONE_NUMBER not set for Zone process}"
        ZONE_CONFIG_DIR="${PROCESS_DIR}/ZoneServerInfo"
        mkdir -p "${ZONE_CONFIG_DIR}"
        sed "s/{{ZONE_NUMBER}}/${ZONE_NUMBER}/g" /docker-config/ZoneServerInfo.txt \
            > "${ZONE_CONFIG_DIR}/ZoneServerInfo.txt"
        echo "Zone ${ZONE_NUMBER} config written to ${ZONE_CONFIG_DIR}/ZoneServerInfo.txt"
        ;;
esac

# --- Step 2: Resolve template variables in ServerInfo.txt ---
# Host networking: all services on 127.0.0.1, ports from env vars.

SA_PASSWORD="${SA_PASSWORD:?SA_PASSWORD not set}"
PUBLIC_IP="${PUBLIC_IP:?PUBLIC_IP env var not set}"

TEMPLATE="/docker-config/ServerInfo/ServerInfo.txt"
SERVER_INFO_DIR="/server/ServerInfo"
DEST="${SERVER_INFO_DIR}/ServerInfo.txt"
mkdir -p "${SERVER_INFO_DIR}"

content=$(cat "${TEMPLATE}")
content="${content//\{\{SA_PASSWORD\}\}/${SA_PASSWORD}}"
content="${content//\{\{PUBLIC_IP\}\}/${PUBLIC_IP}}"

# Resolve port template variables from env vars (base port + offsets for each service)
resolve_ports() {
    local name=$1 base=$2
    content="${content//\{\{${name}_PORT\}\}/${base}}"
    content="${content//\{\{${name}_PORT_1\}\}/$((base + 1))}"
    content="${content//\{\{${name}_PORT_2\}\}/$((base + 2))}"
}

resolve_ports LOGIN      "${LOGIN_PORT:-9010}"
resolve_ports WM         "${WM_PORT:-9013}"
resolve_ports ZONE00     "${ZONE00_PORT:-9016}"
resolve_ports ZONE01     "${ZONE01_PORT:-9019}"
resolve_ports ZONE02     "${ZONE02_PORT:-9022}"
resolve_ports ZONE03     "${ZONE03_PORT:-9025}"
resolve_ports ZONE04     "${ZONE04_PORT:-9028}"

# DB bridge and SQL ports (single port each, no offsets)
content="${content//\{\{ACCOUNT_PORT\}\}/${ACCOUNT_PORT:-9031}}"
content="${content//\{\{ACCOUNTLOG_PORT\}\}/${ACCOUNTLOG_PORT:-9032}}"
content="${content//\{\{CHARACTER_PORT\}\}/${CHARACTER_PORT:-9033}}"
content="${content//\{\{GAMELOG_PORT\}\}/${GAMELOG_PORT:-9034}}"
content="${content//\{\{SQL_PORT\}\}/${SQL_PORT:-1433}}"

echo "ServerInfo.txt resolved (PUBLIC_IP=${PUBLIC_IP}, LOGIN_PORT=${LOGIN_PORT:-9010}, SQL_PORT=${SQL_PORT:-1433})"

printf '%s' "${content}" > "${DEST}"
echo "ServerInfo.txt written to ${DEST} ($(wc -c < "${DEST}") bytes)"

# Also write to 9Data path — the volume-mounted game data has an original
# ServerInfo.txt with 127.0.0.1 and .\SQLEXPRESS that must be overwritten.
NINEDATA_DIR="/server/9Data/ServerInfo"
if [ -d "${NINEDATA_DIR}" ]; then
    cp "${DEST}" "${NINEDATA_DIR}/ServerInfo.txt"
    echo "ServerInfo.txt also written to ${NINEDATA_DIR}/"
fi

# --- Step 3: Wine registry keys ---

echo "Setting up registry keys..."
# Ensure Wine uses builtin odbc32.dll (bridges to system unixODBC/FreeTDS).
# Set at runtime to avoid Docker build cache serving stale layers.
WINEDEBUG=-all wine reg add 'HKCU\Software\Wine\DllOverrides' \
    /v odbc32 /t REG_SZ /d builtin /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\Fantasy\Fighter' /v Bird    /d Eagle      /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\Fantasy\Fighter' /v Insect  /d Honet      /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\GBO' /v Desert   /d 138127     /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\GBO' /v Mountain  /d 30324      /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\GBO' /v Natural   /d 126810443  /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\GBO' /v Ocean     /d 7241589632 /f 2>/dev/null
WINEDEBUG=-all wine reg add 'HKLM\Software\Wow6432Node\GBO' /v Sabana    /d 2554545953 /f 2>/dev/null
echo "Registry keys set."

# --- Step 4: GamigoZR service (Zone processes only) ---

if [[ "${PROCESS_NAME}" =~ ^Zone ]]; then
    GAMIGOZR_EXE='Z:\server\GamigoZR\GamigoZR.exe'
    if [ -f /server/GamigoZR/GamigoZR.exe ]; then
        echo "Registering GamigoZR service..."
        # Use cmd /c wrapper — same SCM timeout fix as game processes.
        # GamigoZR does heavy init before StartServiceCtrlDispatcher().
        WINEDEBUG=-all wine sc.exe delete GamigoZR 2>/dev/null || true
        WINEDEBUG=-all wine sc.exe create GamigoZR \
            binPath= "cmd /c ${GAMIGOZR_EXE}" start= demand 2>/dev/null || true
        WINEDEBUG=-all wine sc.exe start GamigoZR 2>/dev/null || \
            echo "WARNING: GamigoZR start failed (may already be running or not supported)"
    else
        echo "WARNING: GamigoZR.exe not found — Zone may crash without it."
    fi
fi

# --- Step 5: Clear old log files ---

LOG_DIR="${PROCESS_DIR}/DebugMessage"
rm -f "${LOG_DIR}"/*.txt 2>/dev/null || true
for pat in Assert ExitLog Msg_ Dbg MapLoad Message Size; do
    rm -f "${PROCESS_DIR}/${pat}"*.txt 2>/dev/null || true
done
echo "Old logs cleared."

# --- Step 6: Register and start Windows service via Wine SCM ---

if [[ "${PROCESS_NAME}" =~ ^Zone([0-9]+)$ ]]; then
    SERVICE_NAME="_Zone${ZONE_NUMBER}"
else
    SERVICE_NAME="_${PROCESS_NAME}"
fi

WIN_EXE="Z:\\server\\${PROCESS_NAME}\\${PROCESS_EXE}"
STDOUT_LOG="${PROCESS_DIR}/stdout.txt"

# Wrap in cmd /c — Wine's SCM kills service processes that take too long
# to call StartServiceCtrlDispatcher(). The game exes do heavy init (IOCP
# threads, config loading) before registering, causing the SCM to time out.
# cmd /c acts as the service wrapper, letting the exe run as a child process.
BIN_PATH="cmd /c ${WIN_EXE} > Z:\\server\\${PROCESS_NAME}\\stdout.txt 2>&1"

echo "Registering service: ${SERVICE_NAME} -> ${WIN_EXE}"
# Delete any stale service registration from previous runs
WINEDEBUG=-all wine sc.exe delete "${SERVICE_NAME}" 2>/dev/null || true
WINEDEBUG=-all wine sc.exe create "${SERVICE_NAME}" \
    binPath= "${BIN_PATH}" start= demand 2>/dev/null || true

# Wine's SCM incorrectly reports services as STOPPED even when the process
# is running fine. Start the service and monitor the actual process instead.
echo "Starting service: ${SERVICE_NAME}"
WINEDEBUG=-all wine sc.exe start "${SERVICE_NAME}" 2>/dev/null || true

# Wait for the exe to appear (Wine SCM takes time to spawn the process)
echo "Waiting for ${PROCESS_EXE} to start..."
STARTED=0
for i in $(seq 1 30); do
    if pgrep -f "${PROCESS_EXE}" > /dev/null 2>&1; then
        echo "${PROCESS_EXE} is running after ${i}s (PID: $(pgrep -f "${PROCESS_EXE}" | head -1))."
        STARTED=1
        break
    fi
    sleep 1
done

if [ "${STARTED}" -ne 1 ]; then
    echo "ERROR: ${PROCESS_EXE} not running after 30s."
    if [ "${KEEP_ALIVE}" = "1" ]; then
        echo "KEEP_ALIVE=1: container staying alive for investigation."
        exec sleep infinity
    fi
    exit 1
fi

# --- Step 7: Wait for log files, then tail them ---

echo "Waiting for log files..."
TIMEOUT=60
for i in $(seq 1 ${TIMEOUT}); do
    log_count=$(find "${PROCESS_DIR}" -maxdepth 1 \
        \( -name "Assert*.txt" -o -name "ExitLog*.txt" -o -name "Msg_*.txt" -o -name "Dbg.txt" \) \
        2>/dev/null | wc -l)
    dir_count=$(find "${LOG_DIR}" -name "*.txt" 2>/dev/null | wc -l)
    if [ "$((log_count + dir_count))" -gt 0 ]; then
        echo "Log files appeared after ${i}s."
        break
    fi
    sleep 1
done

# Tail all log files (including stdout capture) in background
find "${PROCESS_DIR}" "${LOG_DIR}" -name "*.txt" 2>/dev/null \
    | xargs -r tail -F 2>/dev/null &

# --- Step 8: Monitor process until it exits ---
# Wine's SCM is unreliable — monitor the actual process instead.

while pgrep -f "${PROCESS_EXE}" > /dev/null 2>&1; do
    sleep 5
done

echo "=== ${PROCESS_EXE} exited ==="
sleep 2
if [ "${KEEP_ALIVE}" = "1" ]; then
    echo "KEEP_ALIVE=1: container staying alive."
    exec sleep infinity
fi
exit 1
