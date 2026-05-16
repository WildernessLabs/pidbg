#!/usr/bin/env bash
# PiDbg capability detection script v2.
# ROOT_FOLDER is injected by the caller before this script runs.
# Outputs a single JSON object to stdout. All errors go to stderr.
# Idempotent. Safe to run without sudo.
set -uo pipefail

SCHEMA=1
TS=$(date -u +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || echo "")

# Expand ~ in ROOT_FOLDER and default if unset
ROOT_FOLDER="${ROOT_FOLDER:-~/meadow}"
ROOT_FOLDER="${ROOT_FOLDER/#\~/$HOME}"

# ── Host ──────────────────────────────────────────────────────────────────────
ARCH=$(uname -m 2>/dev/null || echo "")
KERNEL=$(uname -r 2>/dev/null || echo "")
# Source /etc/os-release for distro info; tolerate missing file
OS_ID=""; OS_VER=""; OS_PRETTY=""
if [ -f /etc/os-release ]; then
    . /etc/os-release 2>/dev/null || true
    OS_ID="${ID:-}"
    OS_VER="${VERSION_ID:-}"
    OS_PRETTY="${PRETTY_NAME:-}"
fi
HOSTNAME=$(hostname -s 2>/dev/null || echo "")
ME=$(id -un 2>/dev/null || echo "")
MY_UID=$(id -u 2>/dev/null || echo "0")

LINGER=false
loginctl show-user "$ME" --property=Linger 2>/dev/null | grep -q "=yes" \
    && LINGER=true || true

# ── Filesystem ────────────────────────────────────────────────────────────────
ROOT_EXISTS=false; ROOT_WRITABLE=false; FREE_BYTES=0
mkdir -p "$ROOT_FOLDER" 2>/dev/null || true
if [ -d "$ROOT_FOLDER" ]; then
    ROOT_EXISTS=true
    [ -w "$ROOT_FOLDER" ] && ROOT_WRITABLE=true || true
    FREE_BYTES=$(df -B1 "$ROOT_FOLDER" 2>/dev/null | awk 'NR==2{print $4+0}' || echo "0")
fi

# ── Daemon ────────────────────────────────────────────────────────────────────
DAEMON_BIN="$ROOT_FOLDER/bin/meadow-daemon"
D_EXISTS=false; D_VER=""; D_SHA256=""; SVC_INST=false; SVC_RUN=false
if [ -f "$DAEMON_BIN" ]; then
    D_EXISTS=true
    D_VER=$("$DAEMON_BIN" --version 2>/dev/null | head -1 | tr -d '\r' || echo "")
    D_SHA256=$(sha256sum "$DAEMON_BIN" 2>/dev/null | awk '{print $1}' || echo "")
fi
systemctl --user is-enabled meadow-daemon 2>/dev/null \
    | grep -q "^enabled" && SVC_INST=true || true
systemctl --user is-active meadow-daemon 2>/dev/null \
    | grep -q "^active"  && SVC_RUN=true  || true

# ── vsdbg ─────────────────────────────────────────────────────────────────────
VSDBG_BIN="$ROOT_FOLDER/vsdbg/vsdbg"
V_EXISTS=false; V_VER=""
if [ -f "$VSDBG_BIN" ]; then
    V_EXISTS=true
    V_VER=$("$VSDBG_BIN" --version 2>/dev/null | head -1 | tr -d '\r' || echo "")
fi

# ── Runtime ───────────────────────────────────────────────────────────────────
DOTNET=""; DOTNET_ROOT=""
if command -v dotnet >/dev/null 2>&1; then
    DOTNET=$(dotnet --version 2>/dev/null | tr -d '\r' || echo "")
    DOTNET_ROOT=$(dirname "$(command -v dotnet)" 2>/dev/null || echo "")
elif [ -f "$HOME/.dotnet/dotnet" ]; then
    DOTNET=$("$HOME/.dotnet/dotnet" --version 2>/dev/null | tr -d '\r' || echo "")
    DOTNET_ROOT="$HOME/.dotnet"
fi
SYSTEMD_USER=false
systemctl --user status 2>/dev/null | grep -qE "State: running" \
    && SYSTEMD_USER=true || true
CURL=false
command -v curl >/dev/null 2>&1 && CURL=true || true

# ── JSON output ───────────────────────────────────────────────────────────────
printf '{\n'
printf '  "schema_version": %d,\n'   "$SCHEMA"
printf '  "timestamp": "%s",\n'      "$TS"
printf '  "host": {\n'
printf '    "arch": "%s",\n'         "$ARCH"
printf '    "kernel": "%s",\n'       "$KERNEL"
printf '    "os_id": "%s",\n'        "$OS_ID"
printf '    "os_version": "%s",\n'   "$OS_VER"
printf '    "os_pretty": "%s",\n'    "$OS_PRETTY"
printf '    "hostname": "%s",\n'     "$HOSTNAME"
printf '    "user": "%s",\n'         "$ME"
printf '    "uid": %s,\n'            "$MY_UID"
printf '    "linger": %s\n'          "$LINGER"
printf '  },\n'
printf '  "filesystem": {\n'
printf '    "root_exists": %s,\n'   "$ROOT_EXISTS"
printf '    "root_writable": %s,\n' "$ROOT_WRITABLE"
printf '    "free_bytes": %s\n'     "$FREE_BYTES"
printf '  },\n'
printf '  "daemon": {\n'
printf '    "binary_exists": %s,\n'     "$D_EXISTS"
printf '    "binary_version": "%s",\n'  "$D_VER"
printf '    "binary_sha256": "%s",\n'   "$D_SHA256"
printf '    "service_installed": %s,\n' "$SVC_INST"
printf '    "service_running": %s\n'    "$SVC_RUN"
printf '  },\n'
printf '  "vsdbg": {\n'
printf '    "binary_exists": %s,\n' "$V_EXISTS"
printf '    "version": "%s"\n'      "$V_VER"
printf '  },\n'
printf '  "runtime": {\n'
printf '    "dotnet_version": "%s",\n'       "$DOTNET"
printf '    "dotnet_root": "%s",\n'         "$DOTNET_ROOT"
printf '    "systemd_user_available": %s,\n' "$SYSTEMD_USER"
printf '    "curl_available": %s\n'          "$CURL"
printf '  }\n'
printf '}\n'
