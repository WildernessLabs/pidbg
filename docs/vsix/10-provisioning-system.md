# 10 — Provisioning System

## Overview

Provisioning turns a bare Raspberry Pi into a debug target: daemon installed, service running, vsdbg available, and all dependencies validated. It runs automatically on the first F5 press and is silent on subsequent presses when nothing has changed.

Provisioning is split into two phases:

| Phase | Who runs it | When | Privilege |
|---|---|---|---|
| **Host bootstrap** | User, once | Before first F5 | `sudo` |
| **VSIX provisioning** | VSIX automatically | F5 (idempotent) | SSH user |

The host bootstrap script creates the directory skeleton and configures linger. Everything after that is orchestrated by the VSIX over SSH with no elevated privilege.

---

## §1 — Provisioning Architecture

### Design principles

- **VSIX is the orchestrator.** All provisioning logic lives in the VSIX; the Pi has no bootstrap agent. The VSIX opens an SSH connection, uploads binaries, runs shell fragments, and polls until healthy.
- **Idempotent by default.** Every step is safe to re-run. Running provisioning ten times on an already-provisioned Pi produces identical state to running it once.
- **Detect before act.** Capability detection runs first and produces a structured result. The VSIX decides what to do; it does not blindly re-install.
- **Fail fast, fail loudly.** Platform checks (architecture, OS) fail immediately before any file transfer. Recoverable failures (missing binary, wrong version) proceed to repair.
- **No silent partial state.** If provisioning fails mid-way, the daemon is either fully installed and running, or not installed at all. Partial installs are detected and cleaned up on the next attempt.

### Two-phase model

```
┌─────────────────────────────────────────────────────────┐
│  PHASE 1: HOST BOOTSTRAP (one-time, user-run)            │
│                                                          │
│  pi$ curl .../setup-meadow.sh | sudo bash                │
│                                                          │
│  Creates:  /opt/meadow/  (owned by pi user)              │
│            /etc/meadow/  (readable by pi user)           │
│  Enables:  loginctl enable-linger $USER                  │
│  Installs: no daemon, no service, no vsdbg               │
└──────────────────────────┬──────────────────────────────┘
                           │ one-time prerequisite
┌──────────────────────────▼──────────────────────────────┐
│  PHASE 2: VSIX PROVISIONING (automatic, repeatable)      │
│                                                          │
│  On every F5:                                            │
│    1. Open SSH connection                                │
│    2. Run capability detection                           │
│    3. Validate platform + runtime                        │
│    4. Install or upgrade daemon binary                   │
│    5. Install or upgrade systemd service                 │
│    6. Start / restart service                            │
│    7. Wait for daemon health                             │
│    8. Install or upgrade vsdbg (via daemon gRPC)         │
│    9. Proceed to deployment                              │
└─────────────────────────────────────────────────────────┘
```

### VSIX provisioning state machine

```
         ┌──────────┐
         │  Start   │
         └────┬─────┘
              │
              ▼
    ┌──────────────────┐    platform invalid     ┌──────────────┐
    │CapabilityDetect  │────────────────────────▶│ ProvisionFail│
    └────────┬─────────┘                         └──────────────┘
             │ detection ok
             ▼
    ┌──────────────────┐    needs host bootstrap  ┌──────────────────────┐
    │ PlatformValidate │────────────────────────▶│PromptHostBootstrap   │
    └────────┬─────────┘                         └──────────────────────┘
             │ platform ok
             ▼
    ┌──────────────────┐
    │ RuntimeValidate  │──── disk full ──────────▶ ProvisionFail
    └────────┬─────────┘
             │ runtime ok
             ▼
    ┌──────────────────┐    already current       ┌─────────────┐
    │  DaemonInstall   │────────────────────────▶│   Deploy    │
    └────────┬─────────┘                         └─────────────┘
             │ installed / upgraded
             ▼
    ┌──────────────────┐
    │ ServiceInstall   │
    └────────┬─────────┘
             │
             ▼
    ┌──────────────────┐
    │  WaitForHealth   │──── timeout ────────────▶ ProvisionFail
    └────────┬─────────┘
             │ daemon healthy
             ▼
    ┌──────────────────┐    already installed     ┌─────────────┐
    │  VsdbgInstall    │────────────────────────▶│   Deploy    │
    └────────┬─────────┘                         └─────────────┘
             │ installed
             ▼
         ┌───────┐
         │Deploy │
         └───────┘
```

### Provisioning contexts

| Context | Trigger | Behavior |
|---|---|---|
| **First install** | F5, daemon not found | Full install path |
| **Already provisioned** | F5, daemon current | Skip all installs, proceed to deploy |
| **Upgrade available** | F5, daemon version < required | Upgrade daemon in place |
| **Repair** | Explicit command or daemon unhealthy | Re-run full provision path |
| **Uninstall** | Explicit command | Remove all installed components |
| **Diagnostics** | Explicit command | Collect and return diagnostic bundle |
| **Offline recovery** | Network-restricted environment | SFTP-only path, no GitHub downloads |

---

## §2 — Capability Detection

Detection runs as a single SSH command that emits a JSON document. The VSIX parses the JSON and makes all provisioning decisions locally — no shell logic on the device makes decisions.

### Detection script

```bash
#!/usr/bin/env bash
# detect.sh — runs on device, emits JSON capability report
# Invoked by VSIX via SSH: bash -s < detect.sh

set -euo pipefail

DAEMON_BIN="${MEADOW_DAEMON_BIN:-/opt/meadow/bin/meadow-daemon}"
VSDBG_DIR="${VSDBG_DIR:-/opt/meadow/vsdbg}"
OPT_MEADOW="/opt/meadow"
ETC_MEADOW="/etc/meadow"
SERVICE_FILE="$HOME/.config/systemd/user/meadow-daemon.service"

# --- helpers ---
bin_ver() { "$1" --version 2>/dev/null | head -1 || echo ""; }
file_sha256() { sha256sum "$1" 2>/dev/null | awk '{print $1}' || echo ""; }
service_state() {
  systemctl --user is-active meadow-daemon 2>/dev/null || echo "inactive"
}
service_enabled() {
  systemctl --user is-enabled meadow-daemon 2>/dev/null || echo "disabled"
}
free_bytes() {
  df --output=avail -B1 "$1" 2>/dev/null | tail -1 | tr -d ' ' || echo "0"
}
linger_enabled() {
  loginctl show-user "$USER" --property=Linger 2>/dev/null | grep -q "yes" && echo true || echo false
}
vsdbg_ver() {
  local f="$VSDBG_DIR/vsdbg-ui"
  [ -f "$f" ] && "$f" --version 2>/dev/null | head -1 || echo ""
}
dotnet_ver() {
  dotnet --version 2>/dev/null || echo ""
}

# --- emit ---
cat <<JSON
{
  "schemaVersion": 1,
  "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
  "host": {
    "arch":        "$(uname -m)",
    "kernel":      "$(uname -r)",
    "os_id":       "$(. /etc/os-release && echo $ID)",
    "os_version":  "$(. /etc/os-release && echo $VERSION_ID)",
    "os_pretty":   "$(. /etc/os-release && echo $PRETTY_NAME)",
    "hostname":    "$(hostname)",
    "user":        "$USER",
    "uid":         "$(id -u)",
    "linger":      $(linger_enabled)
  },
  "filesystem": {
    "opt_meadow_exists":   $([ -d "$OPT_MEADOW" ]   && echo true || echo false),
    "opt_meadow_writable": $([ -w "$OPT_MEADOW" ]   && echo true || echo false),
    "etc_meadow_exists":   $([ -d "$ETC_MEADOW" ]   && echo true || echo false),
    "free_bytes_opt":      $(free_bytes /opt),
    "free_bytes_home":     $(free_bytes "$HOME")
  },
  "daemon": {
    "binary_exists":  $([ -f "$DAEMON_BIN" ] && echo true || echo false),
    "binary_sha256":  "$(file_sha256 "$DAEMON_BIN")",
    "binary_version": "$(bin_ver "$DAEMON_BIN" 2>/dev/null || echo "")",
    "service_file_exists": $([ -f "$SERVICE_FILE" ] && echo true || echo false),
    "service_state":  "$(service_state)",
    "service_enabled": "$(service_enabled)"
  },
  "vsdbg": {
    "dir_exists":    $([ -d "$VSDBG_DIR" ] && echo true || echo false),
    "binary_exists": $([ -f "$VSDBG_DIR/vsdbg-ui" ] && echo true || echo false),
    "version":       "$(vsdbg_ver)"
  },
  "runtime": {
    "dotnet_version":     "$(dotnet_ver)",
    "bash_version":       "$(bash --version | head -1)",
    "curl_available":     $(command -v curl  >/dev/null 2>&1 && echo true || echo false),
    "systemd_user_available": $(systemctl --user status >/dev/null 2>&1 && echo true || echo false)
  }
}
JSON
```

The VSIX runs this with a 10-second timeout. If it times out or fails to produce valid JSON, the VSIX surfaces a connection error — the device is unreachable or misconfigured.

### Detection result shape (C#)

```
DetectionResult
├── SchemaVersion: int
├── Timestamp: DateTimeOffset
├── Host
│   ├── Arch: string          // "aarch64"
│   ├── Kernel: string        // "6.6.31+rpt-rpi-v8"
│   ├── OsId: string          // "raspbian" | "debian"
│   ├── OsVersion: string     // "12"
│   ├── OsPretty: string      // "Raspberry Pi OS (bookworm)"
│   ├── Hostname: string
│   ├── User: string
│   ├── Uid: int
│   └── Linger: bool
├── Filesystem
│   ├── OptMeadowExists: bool
│   ├── OptMeadowWritable: bool
│   ├── EtcMeadowExists: bool
│   ├── FreeBytesOpt: long
│   └── FreeBytesHome: long
├── Daemon
│   ├── BinaryExists: bool
│   ├── BinarySha256: string
│   ├── BinaryVersion: string  // semver, "" if absent
│   ├── ServiceFileExists: bool
│   ├── ServiceState: string   // "active" | "inactive" | "failed"
│   └── ServiceEnabled: string // "enabled" | "disabled"
├── Vsdbg
│   ├── DirExists: bool
│   ├── BinaryExists: bool
│   └── Version: string        // "" if absent
└── Runtime
    ├── DotnetVersion: string  // "" if not installed
    ├── BashVersion: string
    ├── CurlAvailable: bool
    └── SystemdUserAvailable: bool
```

### Detection decision matrix

```
VSIX reads DetectionResult → evaluates in order:

1. host.arch != "aarch64"             → FAIL  (unsupported architecture)
2. host.os_id not in {raspbian,debian}→ FAIL  (unsupported OS)
3. host.os_version < "12"             → FAIL  (OS too old)
4. runtime.systemd_user_available==F  → FAIL  (no systemd user)
5. filesystem.free_bytes_opt < 200MB  → FAIL  (insufficient disk)
6. filesystem.opt_meadow_exists==F    → WARN  (need host bootstrap)
7. filesystem.opt_meadow_writable==F  → WARN  (need host bootstrap)
8. host.linger==false                 → WARN  (service won't survive logout)
9. daemon.binary_exists==F            → INSTALL daemon
10. daemon.binary_version < required  → UPGRADE daemon
11. daemon.binary_sha256 != expected  → REINSTALL daemon (corruption)
12. daemon.service_file_exists==F     → INSTALL service
13. daemon.service_state != "active"  → START service
14. vsdbg.binary_exists==F            → INSTALL vsdbg
15. vsdbg.version < required_vsdbg    → UPGRADE vsdbg
```

---

## §3 — Platform Validation

Platform validation is the first gate after detection. Failures here are permanent — no amount of retrying will fix them.

### Architecture check

```
Requirement:  uname -m == "aarch64"
Error:        "This project targets ARM64. Connected device reports architecture '{arch}'.
               Supported: Raspberry Pi OS 64-bit (aarch64)"
Action:       Abort provisioning. Do not offer repair.
```

32-bit Raspberry Pi OS (`armv7l`, `armhf`) is explicitly not supported. The daemon binary is `linux-arm64` (self-contained), vsdbg requires 64-bit, and .NET 10 ARM32 support is EOL.

### OS check

```
Requirement:  /etc/os-release ID in { "raspbian", "debian", "ubuntu" }
              VERSION_ID >= "12"  (Bookworm)
Error (id):   "Unsupported OS '{os_pretty}'. Supported: Raspberry Pi OS 64-bit (Bookworm),
               Debian 12, Ubuntu 22.04+"
Error (ver):  "OS version '{os_pretty}' is too old. Minimum: Debian 12 / Bookworm.
               Current version uses glibc {detected}, minimum required: 2.35"
Action:       Abort provisioning.
```

### Kernel check (advisory only)

```
Requirement:  kernel >= 5.15  (for io_uring, perf events, memfd)
Warning:      "Kernel {kernel} is older than 5.15. Debugging will work but some
               profiling features may be unavailable."
Action:       Warn only, continue provisioning.
```

### Systemd user session check

```
Requirement:  systemctl --user status  exits 0 or 1 (any non-error)
Error:        "systemd user session is not available. This is required for the
               meadow-daemon service. Ensure PAM is configured for systemd sessions."
Action:       Abort provisioning. Offer link to troubleshooting guide.
```

### Linger check

```
Requirement:  loginctl show-user $USER | grep Linger=yes
Warning:      "Linger is not enabled for user '{user}'. The meadow-daemon service
               will stop when you disconnect from SSH. Run the host bootstrap script
               to enable linger."
Action:       Warn and offer to enable automatically (requires sudo, one-time).
```

### Platform validation checklist

```
[ ] arch == aarch64
[ ] os_id in {raspbian, debian, ubuntu}
[ ] os_version >= 12
[ ] systemd user session available
[ ] /proc/sys/kernel/perf_event_paranoid readable  (advisory)
[ ] kernel >= 5.15  (advisory)
```

---

## §4 — Runtime Validation

Runtime validation checks that the execution environment is ready. These can be repaired without reinstalling the OS.

### Disk space requirements

| Component | Install size | Working space | Total |
|---|---|---|---|
| Daemon binary (self-contained) | ~35 MB | — | 35 MB |
| vsdbg | ~55 MB | — | 55 MB |
| App deployments | varies | staging dir | 2× largest app |
| State / logs | — | ~10 MB/day | 50 MB reserve |
| **Minimum required** | | | **200 MB** |
| **Recommended** | | | **500 MB** |

```
Check:   free_bytes_opt >= 200 * 1024 * 1024
Error:   "Insufficient disk space on /opt. Available: {avail_mb} MB, required: 200 MB.
          Free space on the device and retry."
Action:  Abort provisioning.
```

### /opt/meadow writability check

```
Check:   directory exists AND writable by current user
Warn:    "Directory /opt/meadow does not exist or is not writable. Run the
          host bootstrap script once to create it:
            curl -sSL .../setup-meadow.sh | sudo bash"
Action:  Surface warning with bootstrap instructions. Do not auto-sudo.
```

### systemd --user socket check

```
Check:   $XDG_RUNTIME_DIR/systemd/private exists (created by pam_systemd)
Warn:    "systemd user runtime directory not found at $XDG_RUNTIME_DIR.
          Ensure PAM configuration includes pam_systemd.so."
Action:  Warn, attempt to continue. Service enable may fail.
```

### ptrace permissions check

```
Check:   /proc/sys/kernel/yama/ptrace_scope <= 1
Warn:    "ptrace_scope is {value}. vsdbg requires ptrace_scope <= 1 to attach
          to processes. Current value may prevent debugging."
Action:  Warn only. vsdbg attach failure will surface a clear error at debug time.
```

Note: Raspberry Pi OS ships with `ptrace_scope=1` (restricted to parent processes). vsdbg handles this correctly by running as the same user as the app process. No change is needed.

### Runtime validation checklist

```
[ ] /opt/meadow exists and is writable
[ ] /etc/meadow exists (or creatable)
[ ] free space >= 200 MB on /opt
[ ] $XDG_RUNTIME_DIR set and accessible
[ ] $HOME/.config/systemd/user/ exists or creatable
[ ] ptrace_scope <= 1  (advisory)
[ ] /tmp writable (for staging downloads)
```

---

## §5 — Filesystem Layout

### Full layout after provisioning

```
/opt/meadow/                           # created by host bootstrap (chown pi:pi)
├── bin/
│   ├── meadow-daemon                  # VSIX-deployed: self-contained binary, chmod 755
│   └── meadow-daemon.bak             # upgrade rollback: previous version, chmod 755
├── vsdbg/
│   ├── vsdbg-ui                       # installed by daemon: vsdbg launcher
│   ├── vsdbg                          # installed by daemon: vsdbg binary
│   └── .version                       # installed by daemon: "17.x.x"
├── apps/
│   ├── MyApp/
│   │   ├── debug/                     # active debug slot (atomic rename target)
│   │   ├── staging/                   # in-progress deploy (cleaned on success)
│   │   └── versions/
│   │       ├── 01JPXXX.../            # production version by ULID
│   │       └── active -> versions/01JPXXX.../  # symlink, atomic swap
│   └── .locks/                        # per-app deploy semaphore files
├── state/
│   ├── apps.json                      # persisted AppRecord list (atomic write)
│   └── sessions.json                  # persisted DebugSessionRecord list (atomic write)
└── logs/
    └── daemon.log -> journald          # symlink to journal; actual logs via systemd

/etc/meadow/                           # created by host bootstrap (chmod 755)
└── daemon.conf                        # VSIX-written: daemon configuration overrides

$HOME/.config/
├── systemd/user/
│   └── meadow-daemon.service          # VSIX-written from template
└── meadow/
    └── device.key                     # device identity keypair (daemon-generated)
```

### Host bootstrap creates

```
/opt/meadow/          chown $USER:$USER  chmod 755
/opt/meadow/bin/      chown $USER:$USER  chmod 755
/opt/meadow/vsdbg/    chown $USER:$USER  chmod 755
/opt/meadow/apps/     chown $USER:$USER  chmod 755
/opt/meadow/state/    chown $USER:$USER  chmod 700
/opt/meadow/logs/     chown $USER:$USER  chmod 755
/etc/meadow/          chown root:$USER   chmod 750
```

### VSIX installs

```
/opt/meadow/bin/meadow-daemon         (SFTP upload, chmod 755)
/etc/meadow/daemon.conf               (SFTP upload, chmod 640)
$HOME/.config/systemd/user/meadow-daemon.service  (SFTP upload)
```

### Daemon installs (via gRPC after startup)

```
/opt/meadow/vsdbg/vsdbg-ui            (GetVsDbg.sh or tarball upload)
/opt/meadow/vsdbg/vsdbg               (same)
/opt/meadow/vsdbg/.version            (same)
$HOME/.config/meadow/device.key       (generated on first run)
```

---

## §6 — Service Installation

### Install sequence

```
1. SFTP: upload meadow-daemon to /opt/meadow/bin/meadow-daemon.new
2. SSH:  chmod 755 /opt/meadow/bin/meadow-daemon.new
3. SSH:  mv /opt/meadow/bin/meadow-daemon.new /opt/meadow/bin/meadow-daemon
         (atomic rename — no window where binary is absent)
4. SFTP: upload service file to ~/.config/systemd/user/meadow-daemon.service
5. SFTP: upload daemon.conf to /etc/meadow/daemon.conf
6. SSH:  systemctl --user daemon-reload
7. SSH:  systemctl --user enable meadow-daemon
8. SSH:  systemctl --user start meadow-daemon   (or restart if already running)
9. VSIX: poll gRPC Ping every 2s, timeout 30s
10.VSIX: call GetDeviceInfo to confirm version
```

### Service file (installed by VSIX from template)

```ini
# ~/.config/systemd/user/meadow-daemon.service
# Installed by PiDbg VSIX — do not edit manually

[Unit]
Description=Meadow Daemon — OTA and Remote Debug Service
Documentation=https://github.com/WildernessLabs/pidbg
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
NotifyAccess=main

ExecStart=/opt/meadow/bin/meadow-daemon
WorkingDirectory=/opt/meadow

Restart=on-failure
RestartSec=3s
StartLimitBurst=5
StartLimitIntervalSec=60s

StandardOutput=journal
StandardError=journal
SyslogIdentifier=meadow-daemon

Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
Environment=DOTNET_USE_POLLING_FILE_WATCHER=false
Environment=MEADOW_CONFIGDIR=/etc/meadow

[Install]
WantedBy=default.target
```

### service install verification

```bash
# VSIX runs after systemctl start, waits for each:
systemctl --user is-active meadow-daemon     # "active"
systemctl --user is-enabled meadow-daemon    # "enabled"
# Then polls gRPC:
grpc_call Ping → PongResponse within 30s
```

### Host bootstrap script (setup-meadow.sh)

```bash
#!/usr/bin/env bash
# setup-meadow.sh — one-time device preparation
# Usage: curl -sSL https://.../setup-meadow.sh | sudo bash
# Or offline: sudo bash setup-meadow.sh
set -euo pipefail

TARGET_USER="${SUDO_USER:-$(logname 2>/dev/null || echo pi)}"
echo "==> Preparing Meadow device for user: $TARGET_USER"

# Validate arch
ARCH=$(uname -m)
if [ "$ARCH" != "aarch64" ]; then
  echo "ERROR: This setup requires ARM64 (aarch64). Detected: $ARCH"
  exit 1
fi

# Create directory skeleton
install -d -m 755 -o "$TARGET_USER" -g "$TARGET_USER" \
  /opt/meadow \
  /opt/meadow/bin \
  /opt/meadow/vsdbg \
  /opt/meadow/apps \
  /opt/meadow/logs

install -d -m 700 -o "$TARGET_USER" -g "$TARGET_USER" \
  /opt/meadow/state

# /etc/meadow: root-owned but group-readable by target user
TARGET_GID=$(id -g "$TARGET_USER")
install -d -m 750 /etc/meadow
chown "root:$TARGET_GID" /etc/meadow

# Enable linger so user services survive logout
loginctl enable-linger "$TARGET_USER"

# Ensure user systemd service dir exists (may not exist before first login)
sudo -u "$TARGET_USER" mkdir -p "/home/$TARGET_USER/.config/systemd/user"

echo "==> Host bootstrap complete."
echo "    Connect VS to this device and press F5 to complete provisioning."
```

---

## §7 — Upgrade Strategy

### Version comparison

The VSIX embeds the required daemon version as a compile-time constant (e.g. `1.2.0`). During capability detection, the daemon reports its installed version. The VSIX compares using semver.

```
required  = VSIX.RequiredDaemonVersion  // embedded at build time
installed = detection.Daemon.BinaryVersion

if installed == ""            → first install
if installed == required      → no action
if installed > required       → no action (forward compatible)
if installed < required       → upgrade
if sha256(installed) != known → reinstall (binary corruption or tampering)
```

### In-place upgrade sequence

```
1. VSIX: stop service
   SSH: systemctl --user stop meadow-daemon
        (waits for Type=notify cleanup, timeout 10s)

2. VSIX: backup current binary
   SSH: cp /opt/meadow/bin/meadow-daemon /opt/meadow/bin/meadow-daemon.bak

3. VSIX: upload new binary
   SFTP: put meadow-daemon → /opt/meadow/bin/meadow-daemon.new
   SSH:  chmod 755 /opt/meadow/bin/meadow-daemon.new

4. VSIX: atomic swap
   SSH:  mv /opt/meadow/bin/meadow-daemon.new /opt/meadow/bin/meadow-daemon

5. VSIX: update service file (if template changed)
   SFTP: put meadow-daemon.service → ~/.config/systemd/user/meadow-daemon.service
   SSH:  systemctl --user daemon-reload

6. VSIX: start service
   SSH:  systemctl --user start meadow-daemon

7. VSIX: wait for health
   gRPC: Ping with 30s timeout

8. VSIX: verify new version
   gRPC: GetDeviceInfo → confirm DaemonVersion == required
```

### Upgrade flow diagram

```
VSIX                                  Device
 │                                      │
 │──── SSH: systemctl stop ────────────▶│
 │                                      │ (service stops, sd_notify)
 │◀─── exit 0 ─────────────────────────│
 │                                      │
 │──── SSH: cp daemon daemon.bak ──────▶│
 │──── SFTP: upload daemon.new ────────▶│
 │──── SSH: mv daemon.new daemon ──────▶│
 │──── SSH: systemctl start ───────────▶│
 │                                      │ (new binary starts, sd_notify READY)
 │════ gRPC: Ping ═════════════════════▶│
 │◀═══ PongResponse ════════════════════│
 │                                      │
 │════ gRPC: GetDeviceInfo ════════════▶│
 │◀═══ DeviceInfo (version=1.2.0) ══════│
 │                                      │
 │ [upgrade complete, proceed to deploy]│
```

### Vsdbg upgrade

vsdbg upgrades flow through the daemon gRPC after the daemon is running:

```
VSIX: required_vsdbg = "17.x.y"  (embedded constant)
VSIX: detected_vsdbg = detection.Vsdbg.Version

if upgrade needed:
  gRPC: InstallVsdbg(version="17.x.y")
        → daemon: runs GetVsDbg.sh (if curl available)
                  or accepts UploadVsdbgTarball stream (offline)
  VSIX: streams InstallVsdbg response, shows progress in Output window
```

---

## §8 — Rollback Strategy

### Daemon rollback

The `.bak` file persists across upgrade. Rollback is triggered when:
- New daemon fails health check after upgrade (automatic)
- User explicitly runs `PiDbg: Repair Connection` (manual)

```bash
# Automatic rollback (VSIX-orchestrated):
SSH: systemctl --user stop meadow-daemon
SSH: mv /opt/meadow/bin/meadow-daemon /opt/meadow/bin/meadow-daemon.failed
SSH: mv /opt/meadow/bin/meadow-daemon.bak /opt/meadow/bin/meadow-daemon
SSH: systemctl --user start meadow-daemon
# then re-validate health
```

After rollback, the VSIX surfaces a warning: "Daemon upgrade failed. Rolled back to {previous_version}. Debugging will continue. Check Output window for details."

### Rollback availability

| Scenario | Rollback available | Action |
|---|---|---|
| Upgrade fails health check | Yes (.bak exists) | Auto-rollback |
| Fresh install fails | No | Clean uninstall, retry |
| Service file corrupt | Yes | Re-upload from VSIX |
| State files corrupt | No rollback needed | Daemon recreates on startup |
| vsdbg upgrade fails | No .bak for vsdbg | Re-install via VSIX |

### No-rollback states

If `.bak` is missing (first install that failed, or manual deletion), the VSIX performs a clean uninstall and full reinstall rather than attempting partial rollback.

---

## §9 — Repair Strategy

Repair is triggered when:
- User runs `PiDbg: Repair Connection` command
- Auto-repair: VSIX detects daemon is in `failed` state at F5
- Auto-repair: gRPC health check fails after service is `active`

### What repair fixes

```
[ ] Binary missing or corrupt     → re-upload from VSIX
[ ] Service file missing          → re-write from template
[ ] Service failed state          → systemctl reset-failed, restart
[ ] Service disabled              → re-enable
[ ] vsdbg missing or corrupt      → reinstall via daemon
[ ] Config file missing           → re-upload defaults
[ ] State files corrupt           → daemon recreates on startup (delete + restart)
[ ] /opt/meadow permissions wrong → cannot fix without sudo (surface instructions)
[ ] Linger disabled               → surface instructions (requires sudo)
```

### Repair is idempotent install

The repair path and the install path are the same code path. The VSIX runs the full capability detection → decision matrix → provisioning sequence. The only difference is the user was explicit that something is broken.

### State file repair

If `apps.json` or `sessions.json` are corrupt (failed JSON parse on daemon startup), the daemon logs a warning and recreates them empty. In-flight debug sessions are abandoned (vsdbg PIDs are released). Running app processes are not killed — the daemon reconciles them via `/proc` scan on startup.

---

## §10 — Uninstall Strategy

### What uninstall removes

```
/opt/meadow/bin/meadow-daemon          removed
/opt/meadow/bin/meadow-daemon.bak     removed
/opt/meadow/vsdbg/                    removed (entire directory)
/opt/meadow/state/                    removed (state files)
/opt/meadow/logs/                     removed (log files)
/etc/meadow/daemon.conf               removed
~/.config/systemd/user/meadow-daemon.service  removed
```

### What uninstall preserves

```
/opt/meadow/apps/                     preserved (user app data)
/opt/meadow/                          preserved (directory skeleton)
/etc/meadow/                          preserved (directory)
~/.config/meadow/device.key           preserved (device identity)
```

The VSIX surfaces a post-uninstall message: "Apps and configuration preserved at /opt/meadow/apps. Run 'sudo rm -rf /opt/meadow' to remove everything."

### Uninstall sequence

```bash
# VSIX-orchestrated over SSH:

# 1. Stop and disable service
systemctl --user stop meadow-daemon    || true
systemctl --user disable meadow-daemon || true
systemctl --user daemon-reload

# 2. Kill any orphan vsdbg processes
pkill -u "$USER" -f vsdbg-ui           || true

# 3. Remove binaries and service
rm -f /opt/meadow/bin/meadow-daemon
rm -f /opt/meadow/bin/meadow-daemon.bak
rm -rf /opt/meadow/vsdbg
rm -f ~/.config/systemd/user/meadow-daemon.service
rm -f /etc/meadow/daemon.conf

# 4. Clean state (optional — VSIX prompts user)
rm -f /opt/meadow/state/apps.json
rm -f /opt/meadow/state/sessions.json
```

### Full uninstall (with app data)

Available as a separate command `PiDbg: Full Uninstall`. Prompts for confirmation, then additionally removes `/opt/meadow/apps/` and the full `/opt/meadow/` directory.

---

## §11 — Logging Strategy

### Provisioning log stream

All provisioning steps emit structured log events to the VSIX Output window under the "PiDbg Provisioning" pane. Each event includes:

```
[HH:MM:SS] [LEVEL] STEP: message
```

Example stream:

```
[14:23:01] [INFO]  detect: running capability detection on pi@raspberrypi
[14:23:02] [INFO]  detect: arch=aarch64, os=Raspberry Pi OS 12 (bookworm)
[14:23:02] [INFO]  detect: daemon not installed, vsdbg not installed
[14:23:02] [INFO]  validate: platform OK, runtime OK, disk 8.4 GB free
[14:23:02] [INFO]  install: uploading meadow-daemon (34.2 MB)...
[14:23:04] [INFO]  install: upload complete (34.2 MB in 2.1s, 16.3 MB/s)
[14:23:04] [INFO]  service: installing systemd unit
[14:23:04] [INFO]  service: enabling meadow-daemon.service
[14:23:04] [INFO]  service: starting meadow-daemon.service
[14:23:06] [INFO]  health: daemon healthy (version 1.0.0, startup 1.8s)
[14:23:06] [INFO]  vsdbg: installing vsdbg 17.x.x...
[14:23:14] [INFO]  vsdbg: installed vsdbg 17.x.x (8.3s)
[14:23:14] [INFO]  provisioning complete (13.2s total)
```

### Provisioning log file

The VSIX writes a timestamped provisioning log to the local machine for diagnostics:

```
%LOCALAPPDATA%\PiDbg\Logs\provision-{hostname}-{timestamp}.log
```

Format is JSON-lines (one event per line) for programmatic analysis.

### Daemon logs during provisioning

After the daemon starts, its logs are available via:

```bash
# On device:
journalctl --user-unit meadow-daemon -f

# Via VSIX gRPC (after daemon is healthy):
gRPC: StreamLogs → LogEvent stream
```

---

## §12 — Diagnostics Strategy

### Diagnostics command

`PiDbg: Run Diagnostics` (command palette) collects a full diagnostic bundle and displays it in a VS window.

### Diagnostics bundle

```
PiDbg Diagnostics — raspberrypi — 2026-05-09T14:30:00Z
═══════════════════════════════════════════════════════

PLATFORM
  Arch:            aarch64 ✓
  OS:              Raspberry Pi OS 12 (bookworm) ✓
  Kernel:          6.6.31+rpt-rpi-v8
  User:            pi (uid=1000)
  Linger:          enabled ✓

FILESYSTEM
  /opt/meadow:     exists, writable ✓
  /etc/meadow:     exists ✓
  Free space:      8.4 GB on /opt ✓

DAEMON
  Binary:          /opt/meadow/bin/meadow-daemon ✓
  Version:         1.0.0 ✓ (required: 1.0.0)
  SHA-256:         a3f2...b91c ✓ (matches expected)
  Service:         active, enabled ✓
  Uptime:          4h 23m
  Restart count:   0

VSDBG
  Binary:          /opt/meadow/vsdbg/vsdbg-ui ✓
  Version:         17.12.11230 ✓ (required: 17.x.x)

GRPC CONNECTIVITY
  gRPC Ping:       ✓ (round-trip: 3ms)
  GetDeviceInfo:   ✓

RUNTIME
  .NET:            not installed (not required — self-contained) ✓
  curl:            installed ✓
  systemd --user:  available ✓

RECENT DAEMON LOGS (last 20 lines)
  [14:23:06] info  Meadow.Daemon started, version 1.0.0
  ...

OPEN DEBUG SESSIONS
  (none)

MANAGED APPS
  (none)
```

### Diagnostics shell script

The VSIX can run a standalone diagnostics script over SSH when the daemon is not running (no gRPC available):

```bash
#!/usr/bin/env bash
# diag.sh — standalone diagnostics (no daemon required)
# Output: structured text report

set -euo pipefail
DAEMON_BIN="/opt/meadow/bin/meadow-daemon"
SERVICE="meadow-daemon"

echo "=== PiDbg Diagnostics ==="
echo "Timestamp: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "Host:      $(hostname)"
echo ""
echo "--- Platform ---"
echo "Arch:    $(uname -m)"
echo "OS:      $(. /etc/os-release && echo "$PRETTY_NAME")"
echo "Kernel:  $(uname -r)"
echo "User:    $USER (uid=$(id -u))"
echo "Linger:  $(loginctl show-user "$USER" --property=Linger 2>/dev/null | cut -d= -f2)"
echo ""
echo "--- Filesystem ---"
echo "/opt/meadow:  $([ -d /opt/meadow ] && echo 'exists' || echo 'MISSING')"
echo "  writable:   $([ -w /opt/meadow ] && echo 'yes' || echo 'NO')"
echo "  free space: $(df -h /opt 2>/dev/null | tail -1 | awk '{print $4}')"
echo "/etc/meadow:  $([ -d /etc/meadow ] && echo 'exists' || echo 'MISSING')"
echo ""
echo "--- Daemon ---"
echo "Binary:   $([ -f "$DAEMON_BIN" ] && echo 'present' || echo 'MISSING')"
[ -f "$DAEMON_BIN" ] && echo "  version: $("$DAEMON_BIN" --version 2>/dev/null || echo unknown)"
[ -f "$DAEMON_BIN" ] && echo "  sha256:  $(sha256sum "$DAEMON_BIN" | awk '{print $1}')"
echo "Service:  $(systemctl --user is-active $SERVICE 2>/dev/null || echo 'inactive')"
echo "Enabled:  $(systemctl --user is-enabled $SERVICE 2>/dev/null || echo 'disabled')"
echo ""
echo "--- Recent logs ---"
journalctl --user-unit $SERVICE -n 20 --no-pager 2>/dev/null || echo "(no journal entries)"
echo ""
echo "--- vsdbg ---"
VSDBG="/opt/meadow/vsdbg/vsdbg-ui"
echo "Binary:  $([ -f "$VSDBG" ] && echo 'present' || echo 'MISSING')"
[ -f "$VSDBG" ] && echo "Version: $("$VSDBG" --version 2>/dev/null | head -1)"
echo ""
echo "=== End Diagnostics ==="
```

### Offline diagnostics bundle export

`PiDbg: Export Diagnostics Bundle` creates a zip file on the local machine containing:

```
pidbg-diag-{hostname}-{timestamp}.zip
├── detection.json          (raw detection output)
├── diag.txt                (diagnostics script output)
├── daemon-logs.txt         (last 500 journal lines)
├── service-file.txt        (cat meadow-daemon.service)
├── daemon-conf.txt         (cat daemon.conf, secrets redacted)
└── provision-log.jsonl     (VSIX provisioning log)
```

This bundle can be attached to a GitHub issue or sent to support.

---

## §13 — Security Considerations

### Privilege model

```
Host bootstrap (sudo):
  Creates /opt/meadow/ owned by $USER
  Enables linger
  That's all — no persistent elevated access

VSIX provisioning (SSH as $USER):
  Uploads binaries to paths owned by $USER
  Starts systemd user service (no root)
  No sudo, no setuid, no capabilities

Daemon runtime ($USER):
  Runs as the deploying user
  ptrace: same-UID ptrace is always allowed (ptrace_scope=1 is fine)
  No CAP_NET_ADMIN, no CAP_SYS_PTRACE needed
  vsdbg: runs as same user, attaches to same-user processes
```

### File permissions

```
/opt/meadow/               755  $USER:$USER
/opt/meadow/bin/           755  $USER:$USER
/opt/meadow/bin/meadow-daemon  755  $USER:$USER  (executable, not setuid)
/opt/meadow/state/         700  $USER:$USER  (private state, mode 700)
/opt/meadow/apps/          755  $USER:$USER
/etc/meadow/               750  root:$USER_GID
/etc/meadow/daemon.conf    640  root:$USER_GID  (readable by daemon, not world)
```

### SSH key handling

The VSIX stores SSH credentials in the VS credential store (via VS `IVsPasswordManager`). On first connect, the user is prompted for credentials. Subsequent connects use stored credentials. Passwords are never written to disk as plaintext.

SSH key-based auth is preferred over password. See §14 Authentication Flow.

### Binary integrity

The VSIX embeds SHA-256 hashes of known daemon binaries. After upload, the VSIX:
1. Requests the device to compute `sha256sum /opt/meadow/bin/meadow-daemon`
2. Compares against the embedded expected hash
3. Aborts provisioning if mismatch (upload corruption or MITM)

### gRPC security posture

The daemon binds `127.0.0.1:50051` only. The VSIX accesses it via SSH port-forward (`ForwardedPortLocal`). No gRPC traffic is exposed on the network. The SSH session is the authentication boundary.

### No root daemon

The daemon does not run as root and does not require root. It does not use `sudo`, `su`, `pkexec`, or capabilities. If a future feature requires elevated privileges (e.g., raw socket capture), it will be implemented as a separate capability-granted sidecar, not by elevating the main daemon.

---

## §14 — Authentication Flow

### First connect (password)

```
VSIX                              VS Credential Store
  │                                      │
  │── lookup(host, user) ───────────────▶│
  │◀─ not found ─────────────────────────│
  │                                      │
  │── ShowConnectDialog() ─────────────▶[user enters host, user, password]
  │◀─ credentials ───────────────────────│
  │                                      │
  │── SSH connect (password auth) ──────▶[device]
  │◀─ connected ─────────────────────────│
  │                                      │
  │── store(host, user, password) ──────▶│ (encrypted by VS credential store)
  │                                      │
  │── [provisioning proceeds] ───────────│
```

### SSH key installation (automatic, on first connect)

After successful password authentication, the VSIX checks for and installs an SSH public key:

```
VSIX                              Device
  │                                  │
  │── check ~/.ssh/id_pidbg.pub ─────│ (local VSIX keystore)
  │                                  │
  │  [if not found: generate RSA 4096 keypair, store in %LOCALAPPDATA%\PiDbg\ssh\]
  │                                  │
  │── SSH: read ~/.ssh/authorized_keys│
  │── [if key not present:]           │
  │── SSH: append public key ────────▶│
  │── SSH: chmod 600 authorized_keys ▶│
  │                                  │
  │── reconnect with key auth ───────▶│
  │── [if key auth succeeds:]         │
  │── update credential store:        │
  │   use key from now on ────────────│
```

After key installation, the VSIX updates stored credentials to use key-based auth. Password auth is no longer used for this host.

### SSH key storage

```
%LOCALAPPDATA%\PiDbg\ssh\
├── id_pidbg                   (private key, permission: current user only)
├── id_pidbg.pub               (public key)
└── known_hosts                (per-host fingerprints)
```

The private key is protected with the current Windows user's DPAPI encryption when at rest.

### Known hosts verification

On first connect, the VSIX shows the device's SSH fingerprint and prompts the user to verify and trust it. The fingerprint is stored in `known_hosts`. Subsequent connects verify against the stored fingerprint. Fingerprint mismatch surfaces an explicit warning (possible MITM).

### Project-level auth config

The VSIX project properties page allows configuring:
```xml
<PropertyGroup>
  <PiDbgHost>raspberrypi.local</PiDbgHost>
  <PiDbgUser>pi</PiDbgUser>
  <PiDbgSshPort>22</PiDbgSshPort>
  <PiDbgSshKeyFile></PiDbgSshKeyFile>   <!-- override key path; empty = use PiDbg default -->
</PropertyGroup>
```

If `PiDbgSshKeyFile` is set, the VSIX uses that key directly without generating its own.

---

## §15 — Version Negotiation

### Version matrix

Every VSIX build embeds a `VersionManifest`:

```json
{
  "vsixVersion":         "1.2.0",
  "requiredDaemonMin":   "1.0.0",
  "requiredDaemonMax":   "1.x.x",
  "preferredDaemon":     "1.2.0",
  "requiredVsdbgMin":    "17.0.0",
  "preferredVsdbg":      "17.12.11230",
  "protoVersion":        1,
  "minProtoVersion":     1
}
```

### Compatibility rules

```
if daemon_version < requiredDaemonMin   → upgrade daemon (mandatory)
if daemon_version > requiredDaemonMax   → warn "newer daemon, may have incompatibilities"
if daemon_version == preferredDaemon    → ideal, no action
if daemon_proto_version < minProtoVersion → fail "incompatible protocol, upgrade daemon"

vsdbg:
if vsdbg_version < requiredVsdbgMin    → upgrade vsdbg (mandatory)
if vsdbg_version == preferredVsdbg     → ideal, no action
if vsdbg_version > preferredVsdbg      → allow (vsdbg is backward-compatible)
```

### Proto version negotiation

The daemon returns its `protoVersion` in `PongResponse`. The VSIX compares against `minProtoVersion`:

```
if pong.ProtoVersion < minProtoVersion
  → block: "Daemon protocol version {pong.ProtoVersion} is too old.
            This VSIX requires protocol {minProtoVersion}+.
            Upgrade the daemon by running PiDbg: Repair Connection."
```

Proto versions are monotone integers. A proto version bump means a breaking change. Minor additions (new optional fields) do not bump the proto version.

### Daemon version reporting

The daemon reports its version in two places:
1. `--version` flag (for detection before startup)
2. `GetDeviceInfo` gRPC response (for post-startup verification)

Both must agree. If they differ, the daemon binary has been replaced without restarting the service — the VSIX restarts the service.

### Upgrade channel

In normal operation, the VSIX always installs its own bundled daemon binary. There is no auto-update channel for the daemon separate from the VSIX update. Daemon version follows VSIX version.

### Version negotiation sequence

```
VSIX                              Device
  │                                  │
  │── detection (--version flag) ───▶│
  │◀─ "0.9.0" ───────────────────────│
  │                                  │
  │  [0.9.0 < required 1.0.0 → upgrade]
  │                                  │
  │── [install new binary] ─────────▶│
  │── [start service] ───────────────▶│
  │                                  │
  │══ gRPC: Ping ═══════════════════▶│
  │◀═ PongResponse(proto_version=1) ══│
  │                                  │
  │  [proto 1 >= min 1 → OK]
  │                                  │
  │══ gRPC: GetDeviceInfo ══════════▶│
  │◀═ DeviceInfo(version="1.2.0") ════│
  │                                  │
  │  [1.2.0 == preferred → no upgrade]
  │  [version negotiation complete]
```

---

## Install Flow Diagram

```
F5 pressed
     │
     ▼
┌────────────────────────┐
│  Open SSH connection   │──── fail ─────▶ "Cannot connect to {host}"
└──────────┬─────────────┘
           │ connected
           ▼
┌────────────────────────┐
│  Run detect.sh         │──── timeout ──▶ "Detection timed out (10s)"
└──────────┬─────────────┘
           │ JSON received
           ▼
┌────────────────────────┐
│  arch == aarch64?      │──── no ───────▶ FAIL "Unsupported architecture"
└──────────┬─────────────┘
           │ yes
           ▼
┌────────────────────────┐
│  OS supported?         │──── no ───────▶ FAIL "Unsupported OS"
└──────────┬─────────────┘
           │ yes
           ▼
┌────────────────────────┐
│  /opt/meadow writable? │──── no ───────▶ WARN "Run setup-meadow.sh"
└──────────┬─────────────┘                (show instructions, abort)
           │ yes
           ▼
┌────────────────────────┐
│  disk >= 200 MB?       │──── no ───────▶ FAIL "Insufficient disk space"
└──────────┬─────────────┘
           │ yes
           ▼
┌────────────────────────────────────────────┐
│  daemon version check                      │
│                                            │
│  not found ─────────────────▶ install      │
│  version < required ────────▶ upgrade      │
│  sha256 mismatch ───────────▶ reinstall    │
│  version ok, sha256 ok ─────▶ skip         │
└──────────┬─────────────────────────────────┘
           │
           ▼
┌────────────────────────┐
│  service running?      │
│  yes + current ────────┼──────────────────▶ skip to vsdbg check
│  no / failed ──────────┼──────────────────▶ (re)install + start
└──────────┬─────────────┘
           │ service started
           ▼
┌────────────────────────┐
│  wait for gRPC health  │──── 30s timeout ─▶ FAIL "Daemon did not start"
└──────────┬─────────────┘                   (rollback if .bak exists)
           │ healthy
           ▼
┌────────────────────────┐
│  vsdbg version check   │
│  not found / too old ──┼──────────────────▶ InstallVsdbg gRPC
│  version ok ───────────┼──────────────────▶ skip
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  linger enabled?       │──── no ───────────▶ WARN (non-blocking)
└──────────┬─────────────┘
           │
           ▼
     PROVISIONING COMPLETE
     → proceed to deployment
```

## Upgrade Flow Diagram

```
F5 pressed (daemon installed, upgrade available)
     │
     ▼
┌────────────────────────┐
│  detect: version check │
│  installed: 1.0.0      │
│  required:  1.2.0      │──── upgrade needed
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  backup current binary │  cp meadow-daemon meadow-daemon.bak
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  stop service          │  systemctl --user stop meadow-daemon
└──────────┬─────────────┘
           │ stopped (max 10s)
           ▼
┌────────────────────────┐
│  upload new binary     │  SFTP meadow-daemon → meadow-daemon.new
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  atomic swap           │  mv meadow-daemon.new meadow-daemon
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  update service file?  │──── changed ─────▶ SFTP + daemon-reload
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐
│  start service         │  systemctl --user start meadow-daemon
└──────────┬─────────────┘
           │
           ▼
┌────────────────────────┐        ┌────────────────────┐
│  wait for health 30s   │─ fail ─▶  rollback .bak      │
└──────────┬─────────────┘        │  restart service    │
           │ healthy               │  surface warning    │
           ▼                      └────────────────────┘
┌────────────────────────┐
│  verify version        │  GetDeviceInfo → confirm 1.2.0
└──────────┬─────────────┘
           │
           ▼
     UPGRADE COMPLETE
     → proceed to deployment
```

---

## Validation Checklists

### Pre-provision checklist (VSIX runs, not user-facing)

```
PLATFORM
  [ ] arch == aarch64
  [ ] os_id in {raspbian, debian, ubuntu}
  [ ] os_version_id >= 12
  [ ] systemd user session available

FILESYSTEM
  [ ] /opt/meadow exists
  [ ] /opt/meadow writable by SSH user
  [ ] /etc/meadow exists
  [ ] free_bytes_opt >= 200MB (200 * 1024 * 1024)
  [ ] $HOME/.config/systemd/user/ exists or creatable

RUNTIME
  [ ] bash >= 5.0
  [ ] xdg_runtime_dir set
  [ ] linger enabled  (warn if not)
```

### Post-provision checklist (VSIX verifies)

```
DAEMON
  [ ] /opt/meadow/bin/meadow-daemon exists
  [ ] sha256 matches expected
  [ ] version == required
  [ ] service state == "active"
  [ ] service enabled == "enabled"
  [ ] gRPC Ping responds within 5s
  [ ] GetDeviceInfo version matches binary --version

VSDBG
  [ ] /opt/meadow/vsdbg/vsdbg-ui exists
  [ ] version >= required_vsdbg_min

DIRECTORIES
  [ ] /opt/meadow/apps/ exists
  [ ] /opt/meadow/state/ exists
  [ ] /opt/meadow/vsdbg/ exists
```

### Manual verification checklist (user troubleshooting)

```bash
# Run on the Pi:

# Platform
uname -m                                      # must be aarch64
. /etc/os-release && echo "$PRETTY_NAME"      # Raspberry Pi OS (bookworm)

# Directories
ls -la /opt/meadow/                           # exists, owned by pi
ls -la /etc/meadow/                           # exists

# Daemon
ls -la /opt/meadow/bin/meadow-daemon          # exists, -rwxr-xr-x
/opt/meadow/bin/meadow-daemon --version       # prints version

# Service
systemctl --user status meadow-daemon         # active (running)
systemctl --user is-enabled meadow-daemon     # enabled

# Linger
loginctl show-user $USER --property=Linger    # Linger=yes

# vsdbg
ls -la /opt/meadow/vsdbg/vsdbg-ui             # exists, executable
/opt/meadow/vsdbg/vsdbg-ui --version          # prints version

# gRPC (requires socat or grpcurl)
# journalctl --user-unit meadow-daemon -n 50  # recent logs
```

---

## Offline Recovery

When the Pi has no internet access (no `curl`, no GitHub), the VSIX falls back to:

### Daemon: always offline-capable

The daemon binary is bundled inside the VSIX. Upload always uses SFTP from the local machine — no download from the internet is ever required.

### vsdbg: offline tarball upload

The VSIX includes a bundled vsdbg tarball as an embedded resource. If the daemon reports `curl` is unavailable or the `InstallVsdbg` gRPC call returns a download failure, the VSIX falls back to `UploadVsdbgTarball` streaming RPC:

```
gRPC: UploadVsdbgTarball(stream)
  VSIX: reads bundled vsdbg-{version}-linux-arm64.tar.gz from embedded resources
  VSIX: streams chunks via UploadVsdbgTarball request stream
  Daemon: accumulates, verifies SHA-256, extracts to /opt/meadow/vsdbg/
```

The bundled vsdbg tarball is ~55 MB and adds to the VSIX `.vsix` package size. This is acceptable — VS extension packages routinely include large binaries.

### Offline recovery checklist

```
[ ] Daemon binary: always available (bundled in VSIX)
[ ] Service file: always available (generated from template)
[ ] daemon.conf: always available (defaults in VSIX)
[ ] vsdbg: bundled tarball fallback
[ ] setup-meadow.sh: bundled in VSIX, SFTP-uploadable
[ ] No dependency on GitHub, NuGet, or any external CDN at provision time
```
