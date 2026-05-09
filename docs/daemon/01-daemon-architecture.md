# Meadow.Daemon — Redesign Architecture

---

## 1. What This Document Covers

The existing Meadow.Daemon is a Rust binary that manages OTA application updates via
MQTT and a REST API on port 5000. This document designs a **C# .NET 10 replacement**
that assumes all of the Rust daemon's responsibilities *and* adds the VS remote debugging
capabilities previously designed as a separate `PiDbg.Agent`.

The result is a single unified daemon: `meadow-daemon`. The separate `PiDbg.Agent`
concept from the earlier pidbg architecture is retired. Meadow.Daemon IS the agent.

### What is retained from the Rust daemon
- OTA update management (MPAK/cloud update flow)
- REST API on `127.0.0.1:5000` — backward-compatible with existing Meadow cloud infrastructure
- Process lifecycle management (stop/restart managed app for updates)
- SSH key-based device identity

### What is added
- gRPC service on `127.0.0.1:50051` — primary API for VS debugging
- VS remote debug orchestration (vsdbg management, session lifecycle)
- Structured deployment with versioning, rollback, retention
- Stdout/stderr streaming
- Health and diagnostics endpoints
- Self-update via gRPC
- systemd `sd_notify` readiness signalling
- Structured JSON logging to systemd journal

### What is removed
- Direct MQTT handling (delegated to a separate background `OtaSubscriberService`)
- All Rust dependencies (the entire Rust toolchain is no longer needed on the Pi)

---

## 2. System Context

```
Developer Machine
    PiDbg VSIX
        │
        │  SSH (port 22) — all traffic tunneled
        │   ├── gRPC tunnel  → Pi:50051  (MeadowDaemonService)
        │   ├── vsdbg tunnel → Pi:4024+  (vsdbg TCP server)
        │   └── SFTP                     (file transfer)
        │
Raspberry Pi ARM64
    ┌─────────────────────────────────────────┐
    │  meadow-daemon.service  (systemd user)  │
    │                                         │
    │  gRPC  127.0.0.1:50051                  │
    │  REST  127.0.0.1:5000  (compat)         │
    │                                         │
    │  ┌─────────────────────────────────┐    │
    │  │  MeadowDaemonService (gRPC)     │    │
    │  ├─────────────────────────────────┤    │
    │  │  DeploymentManager              │    │
    │  │  ProcessManager                 │    │
    │  │  VsdbgManager                   │    │
    │  │  DebugSessionManager            │    │
    │  │  OtaUpdateService  (background) │    │
    │  │  HealthService                  │    │
    │  └─────────────────────────────────┘    │
    │                                         │
    │  vsdbg  127.0.0.1:4024+ (on demand)    │
    │  Managed app  (lifecycle by daemon)     │
    └─────────────────────────────────────────┘
```

---

## 3. Hosting Model

The daemon uses `Microsoft.Extensions.Hosting` (Generic Host) with Kestrel as the gRPC
server. This is the same hosting model used by ASP.NET Core microservices.

```
Program.cs
└── Host.CreateDefaultBuilder()
    ├── ConfigureAppConfiguration()
    │   ├── appsettings.json (base config)
    │   ├── /etc/meadow/daemon.conf (system override)
    │   └── Environment variables (MEADOW_*)
    ├── ConfigureServices()
    │   ├── Kestrel → HTTP/2 on 127.0.0.1:50051 (gRPC)
    │   ├── Kestrel → HTTP/1.1 on 127.0.0.1:5000 (REST compat)
    │   ├── AddGrpc() + AddGrpcHealthChecks()
    │   ├── AddControllers() (REST compat layer)
    │   ├── MeadowDaemonGrpcService (singleton)
    │   ├── DeploymentManager (singleton)
    │   ├── ProcessManager (singleton)
    │   ├── VsdbgManager (singleton)
    │   ├── DebugSessionManager (singleton)
    │   ├── OtaUpdateService (IHostedService — background)
    │   ├── ProcessMonitorService (IHostedService — background)
    │   ├── HealthReporterService (IHostedService — background)
    │   └── StateStore (singleton)
    └── UseSystemd()                    ← sd_notify integration
```

`UseSystemd()` from `Microsoft.Extensions.Hosting.Systemd` handles:
- Calling `sd_notify(READY=1)` when the host finishes starting
- Calling `sd_notify(STOPPING=1)` on graceful shutdown
- Propagating `systemctl reload` as `IHostApplicationLifetime.ApplicationStopping`

---

## 4. Class Diagram

```
MeadowDaemonGrpcService
│  (implements MeadowDaemonService.MeadowDaemonServiceBase)
│  Thin dispatch layer — no logic, delegates to domain services
│
├── DeploymentManager
│   ├── VersionStore              per-app version index (state/apps.json)
│   ├── StagingController         manages staging dirs
│   └── ManifestVerifier          SHA-256 verification
│
├── ProcessManager
│   ├── ManagedAppState           current state of the supervised app
│   ├── ProcessOutputBroadcaster  Channel<OutputLine> per process
│   └── ProcessExitWatcher        background task per spawned process
│
├── VsdbgManager
│   ├── VsdbgInstaller            downloads/extracts vsdbg
│   └── VsdbgVersionStore         persists installed version
│
├── DebugSessionManager
│   ├── SessionRegistry           active sessions (in-memory + state/sessions.json)
│   └── VsdbgLauncher             spawns vsdbg process
│
├── OtaUpdateService  (IHostedService)
│   ├── MqttSubscriber            paho-mqtt → managed MQTT client
│   ├── CloudAuthClient           JWT/RSA auth with Meadow cloud
│   └── UpdateApplicator          download → extract → stop app → swap → restart
│
├── ProcessMonitorService  (IHostedService)
│   └── Watches tracked PIDs, raises ProcessExited, triggers auto-restart policy
│
├── HealthReporterService  (IHostedService)
│   └── Pushes HealthStatus snapshots to Channel<HealthStatus> for streaming
│
├── StateStore
│   └── Persists apps.json, sessions.json via atomic write (write-temp → rename)
│
└── RestCompatController  (ASP.NET Controller)
    └── Maps /api/* to gRPC service calls (thin shim, no own logic)
```

---

## 5. Project Structure

```
Meadow.Daemon/
├── Source/
│   │
│   ├── Meadow.Daemon/                     # The daemon (was mc-daemon in Rust)
│   │   ├── Meadow.Daemon.csproj
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── GrpcService/
│   │   │   └── MeadowDaemonGrpcService.cs
│   │   ├── Services/
│   │   │   ├── DeploymentManager.cs
│   │   │   ├── VersionStore.cs
│   │   │   ├── StagingController.cs
│   │   │   ├── ManifestVerifier.cs
│   │   │   ├── ProcessManager.cs
│   │   │   ├── ProcessOutputBroadcaster.cs
│   │   │   ├── ProcessExitWatcher.cs
│   │   │   ├── VsdbgManager.cs
│   │   │   ├── VsdbgInstaller.cs
│   │   │   ├── DebugSessionManager.cs
│   │   │   ├── VsdbgLauncher.cs
│   │   │   ├── OtaUpdateService.cs
│   │   │   ├── MqttSubscriber.cs
│   │   │   ├── CloudAuthClient.cs
│   │   │   ├── UpdateApplicator.cs
│   │   │   ├── ProcessMonitorService.cs
│   │   │   ├── HealthReporterService.cs
│   │   │   └── StateStore.cs
│   │   ├── Models/
│   │   │   ├── AppRecord.cs
│   │   │   ├── DeploymentVersion.cs
│   │   │   ├── DebugSessionRecord.cs
│   │   │   └── DaemonState.cs
│   │   ├── RestCompat/
│   │   │   └── MeadowRestCompatController.cs
│   │   └── systemd/
│   │       └── meadow-daemon.service.template
│   │
│   ├── Meadow.Daemon.Contracts/           # Shared proto + generated code
│   │   ├── Meadow.Daemon.Contracts.csproj
│   │   └── proto/
│   │       ├── meadow_daemon.proto        # Unified service definition
│   │       ├── deployment.proto
│   │       ├── process.proto
│   │       ├── session.proto
│   │       └── common.proto
│   │
│   └── Meadow.Daemon.Client/              # Existing C# client library (updated)
│       ├── Meadow.Daemon.Client.csproj
│       └── MeadowDaemonClient.cs          # Typed wrapper over gRPC stub
│
├── tests/
│   ├── Meadow.Daemon.Tests/
│   └── Meadow.Daemon.Integration.Tests/
│
└── scripts/
    ├── install.sh
    ├── uninstall.sh
    └── update.sh
```

### Project targets

| Project | Framework | Runtime | Notes |
|---|---|---|---|
| Meadow.Daemon | net10.0 | linux-arm64 | Self-contained, single-file, trimmed |
| Meadow.Daemon.Contracts | net10.0; netstandard2.1 | Any | Multi-target for clients |
| Meadow.Daemon.Client | netstandard2.1 | Any | Existing client, updated |

---

## 6. Configuration

### /etc/meadow/daemon.conf (system-level, requires sudo)
```json
{
  "Meadow": {
    "GrpcPort": 50051,
    "RestPort": 5000,
    "AppRoot": "/opt/meadow/apps",
    "VsdbgRoot": "/opt/meadow/vsdbg",
    "StateRoot": "/opt/meadow/state",
    "LogRoot": "/opt/meadow/logs",
    "DeploymentRetentionCount": 3,
    "VsdbgPortRangeStart": 4024,
    "VsdbgPortRangeEnd": 4124,
    "ProcessGracefulStopSeconds": 5,
    "AutoRestartManagedApp": true
  },
  "Cloud": {
    "Enabled": true,
    "MqttBroker": "mqtt.meadowcloud.co",
    "MqttPort": 8883,
    "AuthEndpoint": "https://identity.meadowcloud.co/api/...",
    "OtaTopic": "{OID}/ota/{ID}"
  }
}
```

### appsettings.json (daemon defaults, bundled with binary)
Contains safe defaults for all settings. `/etc/meadow/daemon.conf` overrides these.

---

## 7. Concurrency Model

| Resource | Guard | Notes |
|---|---|---|
| Per-app deployment | `SemaphoreSlim(1)` per app name | One deploy at a time per app |
| Global deployment list | `ConcurrentDictionary<string, AppRecord>` | Multiple apps concurrently |
| Process start/stop | `SemaphoreSlim(1)` per app | Prevent concurrent start+stop |
| Debug session registry | `ConcurrentDictionary<string, DebugSessionRecord>` | Multiple sessions (future) |
| vsdbg install | `SemaphoreSlim(1)` global | One install at a time |
| State persistence | `SemaphoreSlim(1)` global | Serialize JSON writes |
| Log event channel | `Channel<LogEvent>` bounded 1000 | Drop-oldest on full |
| Process output | `Channel<OutputLine>` per process bounded 2000 | Drop-oldest on full |
| Health snapshots | `Channel<HealthStatus>` bounded 10 | Replace-on-full |

All gRPC service methods run on Kestrel's thread pool. No `lock` statements — all
synchronization uses `SemaphoreSlim` or `Channel<T>` which are async-compatible.

---

## 8. State Management

### Persistent state

Two JSON files survive daemon restarts:

**`/opt/meadow/state/apps.json`**:
```json
{
  "apps": [
    {
      "name": "MyApp",
      "entryPoint": "MyApp.dll",
      "startupArgs": "--config production.json",
      "environmentVariables": { "DOTNET_ENVIRONMENT": "Production" },
      "activeVersion": "000003",
      "debugVersion": "debug",
      "autoStart": true,
      "pid": 4829,
      "lastStartedAt": "2025-01-15T10:23:45Z"
    }
  ]
}
```

**`/opt/meadow/state/sessions.json`**:
```json
{
  "sessions": [
    {
      "sessionId": "sess-001",
      "appName": "MyApp",
      "vsdbgPid": 4823,
      "vsdbgPort": 4024,
      "appPid": 4829,
      "startedAt": "2025-01-15T10:23:46Z",
      "correlationId": "7f3a2b1c"
    }
  ]
}
```

### Startup state recovery

On startup, `ProcessMonitorService` reconciles state:
1. Load `apps.json` → know which apps should be running
2. For each app with a recorded PID: probe `/proc/<pid>/cmdline` to verify it's the right process
3. If alive + matches → re-adopt (update internal handle, no restart)
4. If dead + `autoStart=true` → schedule restart after 3-second delay
5. For each session in `sessions.json`: check vsdbg PID alive; if dead, clean up session record

This handles power loss correctly. The app continues running (or is restarted) without
the developer needing to do anything.

---

## 9. Migration from Rust Daemon

The Rust `mc-daemon` is replaced entirely. Migration path:

1. `install.sh` stops the old service: `systemctl --user stop mc-daemon`
2. Reads `/etc/meadow.conf` (Rust config format) and migrates to `/etc/meadow/daemon.conf`
3. Installs new `meadow-daemon` binary at `/opt/meadow/daemon/`
4. Installs new systemd unit at `~/.config/systemd/user/meadow-daemon.service`
5. Disables old unit, enables new unit
6. Starts new daemon: `systemctl --user start meadow-daemon`

The REST API on `:5000` is preserved (via `RestCompatController`) so existing Meadow
cloud infrastructure continues working without changes.
