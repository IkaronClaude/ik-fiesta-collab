#!/bin/bash
# Manage secrets for the current project.
#
# Usage:
#   mimir deploy secret set KEY VALUE  - store a secret (gitignored)
#   mimir deploy secret get KEY        - print the current value
#   mimir deploy secret list           - show all required secrets and status
#   mimir deploy secret check          - prompt for any missing secrets
#
# Files:
#   .mimir-deploy.secrets      KEY=VALUE pairs  (gitignored, never commit)
#   .mimir-deploy.secret-keys  KEY names only   (commit this)

source "$(dirname "$0")/common.sh"
shift  # consume project name

SUBCMD="${1:-}"
shift 2>/dev/null || true

SECRETS_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.secrets"
KEYS_FILE="${MIMIR_PROJ_DIR}/.mimir-deploy.secret-keys"

case "${SUBCMD}" in
set)
    CFG_KEY="${1:-}"
    CFG_VAL="${2:-}"
    if [ -z "${CFG_KEY}" ] || [ -z "${CFG_VAL}" ]; then
        echo "Usage: mimir deploy secret set KEY VALUE"
        exit 1
    fi
    # Write value into secrets file
    TMP="$(mktemp)"
    [ -f "${SECRETS_FILE}" ] && grep -v "^${CFG_KEY}=" "${SECRETS_FILE}" > "${TMP}" 2>/dev/null || true
    echo "${CFG_KEY}=${CFG_VAL}" >> "${TMP}"
    mv "${TMP}" "${SECRETS_FILE}"
    # Register key name
    if ! grep -qx "${CFG_KEY}" "${KEYS_FILE}" 2>/dev/null; then
        echo "${CFG_KEY}" >> "${KEYS_FILE}"
    fi
    echo "Set secret ${CFG_KEY} for project ${PROJECT}."
    echo "  Values file (gitignored): ${SECRETS_FILE}"
    echo "  Keys file  (committable): ${KEYS_FILE}"
    ;;

get)
    CFG_KEY="${1:-}"
    if [ -z "${CFG_KEY}" ]; then
        echo "Usage: mimir deploy secret get KEY"
        exit 1
    fi
    if [ -f "${SECRETS_FILE}" ]; then
        value="$(grep "^${CFG_KEY}=" "${SECRETS_FILE}" 2>/dev/null | head -1 | cut -d= -f2-)"
        if [ -n "${value}" ]; then
            echo "${value}"
            exit 0
        fi
    fi
    echo "(not set)"
    ;;

list)
    echo "Secrets for project ${PROJECT}:"
    echo "  Keys file: ${KEYS_FILE}"
    echo ""
    if [ ! -f "${KEYS_FILE}" ]; then
        echo "  (no required secrets defined)"
        echo "  Define secrets with: mimir deploy secret set KEY VALUE"
        exit 0
    fi
    while IFS= read -r key; do
        [ -z "${key}" ] && continue
        if grep -q "^${key}=" "${SECRETS_FILE}" 2>/dev/null; then
            echo "  ${key} = <set>"
        else
            echo "  ${key} = (NOT SET)"
        fi
    done < "${KEYS_FILE}"
    ;;

check)
    echo "Checking required secrets for project ${PROJECT}..."
    if [ ! -f "${KEYS_FILE}" ]; then
        echo "  No required secrets defined."
        exit 0
    fi
    all_set=1
    while IFS= read -r key; do
        [ -z "${key}" ] && continue
        if ! grep -q "^${key}=" "${SECRETS_FILE}" 2>/dev/null; then
            all_set=0
            read -rp "  Enter value for ${key}: " input_val
            TMP="$(mktemp)"
            [ -f "${SECRETS_FILE}" ] && grep -v "^${key}=" "${SECRETS_FILE}" > "${TMP}" 2>/dev/null || true
            echo "${key}=${input_val}" >> "${TMP}"
            mv "${TMP}" "${SECRETS_FILE}"
            echo "  Saved ${key}."
        fi
    done < "${KEYS_FILE}"
    [ "${all_set}" = "1" ] && echo "  All required secrets are set."
    ;;

*)
    echo "Usage: mimir deploy secret <set|get|list|check> [KEY] [VALUE]"
    echo ""
    echo "  set KEY VALUE   Store a secret (gitignored)"
    echo "  get KEY         Print a secret value"
    echo "  list            Show all required secrets and their status"
    echo "  check           Prompt for any missing required secrets"
    exit 1
    ;;
esac
