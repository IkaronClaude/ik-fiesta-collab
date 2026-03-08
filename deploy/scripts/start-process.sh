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

if [ ! -f "${EXE_PATH}" ]; then
    echo "ERROR: Executable not found: ${EXE_PATH}"
    exit 1
fi

echo "=== Starting ${PROCESS_NAME} (${PROCESS_EXE}) under Wine ==="
echo "Process dir: ${PROCESS_DIR}"

# Start a persistent Xvfb (Wine needs X11 even for headless server processes)
Xvfb :99 -screen 0 800x600x24 &
export DISPLAY=:99
sleep 1

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

# --- Step 2: Resolve Docker hostnames to IPs in ServerInfo.txt ---
# Game exes use inet_addr() which requires IP addresses, not hostnames.

SA_PASSWORD="${SA_PASSWORD:?SA_PASSWORD not set}"
TEMPLATE="/docker-config/ServerInfo/ServerInfo.txt"
SERVER_INFO_DIR="/server/ServerInfo"
DEST="${SERVER_INFO_DIR}/ServerInfo.txt"
mkdir -p "${SERVER_INFO_DIR}"

content=$(cat "${TEMPLATE}")
content="${content//\{\{SA_PASSWORD\}\}/${SA_PASSWORD}}"

echo "Resolving Docker hostnames to IPs..."
for hostname in login worldmanager zone00 zone01 zone02 zone03 zone04 \
                account accountlog character gamelog sqlserver; do
    ip=$(getent hosts "${hostname}" 2>/dev/null | awk '{print $1}' | head -1)
    if [ -n "${ip}" ]; then
        echo "  ${hostname} -> ${ip}"
        content="${content//\"${hostname}\"/\"${ip}\"}"
    else
        echo "  WARNING: ${hostname} - no IP found"
    fi
done

printf '%s' "${content}" > "${DEST}"
echo "ServerInfo.txt written to ${DEST} ($(wc -c < "${DEST}") bytes)"

# --- Step 3: Wine registry keys ---

echo "Setting up registry keys..."
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
    GAMIGOZR_EXE='C:\server\GamigoZR\GamigoZR.exe'
    if [ -f /server/GamigoZR/GamigoZR.exe ]; then
        echo "Registering GamigoZR service..."
        WINEDEBUG=-all wine sc.exe create GamigoZR \
            binPath= "${GAMIGOZR_EXE}" start= demand 2>/dev/null || true
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

WIN_EXE="C:\\server\\${PROCESS_NAME}\\${PROCESS_EXE}"

echo "Registering service: ${SERVICE_NAME} -> ${WIN_EXE}"
WINEDEBUG=-all wine sc.exe create "${SERVICE_NAME}" \
    binPath= "${WIN_EXE}" start= demand 2>/dev/null || true

echo "Starting service: ${SERVICE_NAME}"
if ! WINEDEBUG=-all wine sc.exe start "${SERVICE_NAME}" 2>/dev/null; then
    echo "ERROR: Failed to start service ${SERVICE_NAME}."
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

# Tail all log files in background
find "${PROCESS_DIR}" "${LOG_DIR}" -name "*.txt" 2>/dev/null \
    | xargs -r tail -F 2>/dev/null &

# --- Step 8: Monitor service until it stops ---

while true; do
    status=$(WINEDEBUG=-all wine sc.exe query "${SERVICE_NAME}" 2>/dev/null \
        | grep -i "STATE" | awk '{print $NF}' | tr -d '[:space:]')
    if [ "${status}" = "STOPPED" ] || [ "${status}" = "4" ]; then
        echo "=== ${SERVICE_NAME} stopped ==="
        # Drain final log output
        sleep 2
        if [ "${KEEP_ALIVE}" = "1" ]; then
            echo "KEEP_ALIVE=1: container staying alive."
            exec sleep infinity
        fi
        exit 1
    fi
    sleep 5
done
