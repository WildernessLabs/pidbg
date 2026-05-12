#!/usr/bin/env bash
# setup-meadow.sh — One-time device preparation for PiDbg
# Usage: curl -sSL .../setup-meadow.sh | sudo bash
#    or: sudo bash setup-meadow.sh
# Idempotent: safe to run multiple times.
set -euo pipefail

# ── Architecture check ────────────────────────────────────────────────────────
ARCH=$(uname -m)
if [ "$ARCH" != "aarch64" ]; then
    echo "ERROR: ARM64 (aarch64) required. Detected: $ARCH" >&2
    exit 1
fi

# ── Identify target user ──────────────────────────────────────────────────────
TARGET_USER="${SUDO_USER:-$(logname 2>/dev/null || id -un)}"
TARGET_GID=$(id -g "$TARGET_USER")
echo "==> Preparing device for user: $TARGET_USER"

# ── Create directory skeleton ─────────────────────────────────────────────────
install -d -m 755 -o "$TARGET_USER" -g "$TARGET_GID" \
    /opt/meadow \
    /opt/meadow/bin \
    /opt/meadow/vsdbg \
    /opt/meadow/apps \
    /opt/meadow/logs \
    /opt/meadow/tmp

install -d -m 700 -o "$TARGET_USER" -g "$TARGET_GID" \
    /opt/meadow/state

# /etc/meadow: root-owned, group-readable by target user
install -d -m 750 /etc/meadow
chown "root:$TARGET_GID" /etc/meadow

echo "  Created: /opt/meadow/ (owner: $TARGET_USER)"
echo "  Created: /etc/meadow/ (group: $TARGET_GID)"

# ── Enable linger ─────────────────────────────────────────────────────────────
if loginctl show-user "$TARGET_USER" --property=Linger 2>/dev/null | grep -q "=yes"; then
    echo "  Linger already enabled for $TARGET_USER"
else
    loginctl enable-linger "$TARGET_USER"
    echo "  Linger enabled for $TARGET_USER"
fi

# ── Ensure systemd user service directory ─────────────────────────────────────
USER_HOME=$(eval echo "~$TARGET_USER")
SERVICE_DIR="$USER_HOME/.config/systemd/user"
if [ ! -d "$SERVICE_DIR" ]; then
    install -d -m 755 -o "$TARGET_USER" -g "$TARGET_GID" "$SERVICE_DIR"
    echo "  Created: $SERVICE_DIR"
fi

# ── Check systemd user session ────────────────────────────────────────────────
if ! systemctl --user --machine="${TARGET_USER}@.host" is-system-running &>/dev/null; then
    echo "  NOTE: systemd user session not active yet."
    echo "        Reboot or re-login as $TARGET_USER before using PiDbg."
fi

echo ""
echo "==> Host bootstrap complete!"
echo "    Connect Visual Studio to this device and press F5 to finish provisioning."
