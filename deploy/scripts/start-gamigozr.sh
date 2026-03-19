#!/bin/bash
# start-gamigozr.sh - Runs GamigoZR as a standalone shared service.
# GamigoZR is a .NET HTTP server that provides cryptographic challenges
# for zone verification. All zone processes connect to it over the network.
# On Windows it's a single system service — this replicates that for Linux.

set -eo pipefail

BINS_DIR="/server-bins"
GAMIGOZR_DIR="/server/GamigoZR"

echo "=== Starting GamigoZR service ==="

# Copy from read-only volume mount
if [ -d "${BINS_DIR}/GamigoZR" ]; then
    mkdir -p "${GAMIGOZR_DIR}"
    cp -a "${BINS_DIR}/GamigoZR/." "${GAMIGOZR_DIR}/"
    echo "Copied GamigoZR binaries."
else
    echo "ERROR: GamigoZR not found at ${BINS_DIR}/GamigoZR"
    exit 1
fi

# Xvfb for Wine
rm -f /tmp/.X99-lock /tmp/.X11-unix/X99 2>/dev/null || true
Xvfb :99 -screen 0 800x600x24 &
export DISPLAY=:99
sleep 1

# Kill stale wineserver
wineserver -k 2>/dev/null || true

# Register and start as Wine service with cmd /c wrapper
GAMIGOZR_EXE='Z:\server\GamigoZR\GamigoZR.exe'
echo "Registering GamigoZR service..."
WINEDEBUG=-all wine sc.exe delete GamigoZR 2>/dev/null || true
WINEDEBUG=-all wine sc.exe create GamigoZR \
    binPath= "cmd /c ${GAMIGOZR_EXE}" start= demand 2>/dev/null || true
echo "Starting GamigoZR service..."
WINEDEBUG=-all wine sc.exe start GamigoZR 2>/dev/null || true

# Wait for it to be running
echo "Waiting for GamigoZR.exe to start..."
STARTED=0
for i in $(seq 1 30); do
    if pgrep -f "GamigoZR" > /dev/null 2>&1; then
        echo "GamigoZR running after ${i}s."
        STARTED=1
        break
    fi
    sleep 1
done

if [ "${STARTED}" -ne 1 ]; then
    echo "ERROR: GamigoZR not running after 30s."
    exit 1
fi

# Monitor until it exits
while pgrep -f "GamigoZR" > /dev/null 2>&1; do
    sleep 5
done

echo "=== GamigoZR exited ==="
exit 1
