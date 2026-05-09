# PiDbg — Major Services

---

## VSIX-side Services

### DebugSessionOrchestrator
**File:** `src/PiDbg.Vsix/Debug/DebugSessionOrchestrator.cs`  
**Interface:** `IDebugSessionOrchestrator`  
**Lifetime:** Transient (one instance per F5 press)

The central coordinator for a debug launch. Called by `RaspberryPiDebugLaunchProvider`.

Sequence:
1. Resolve device from active profile
2. Acquire (or reuse) `IDeviceConnection`
3. Verify agent is alive (ping with 5-second timeout)
4. Check vsdbg installed; if not, install it (with progress to Output window)
5. Request dotnet build + publish via `IBuildManager`
6. Delegate deployment to `IDeploymentService`
7. Allocate ephemeral local port for vsdbg tunnel
8. Open port forward: `localhost:N → Pi:4024`
9. Call `AgentClient.StartDebugSessionAsync()` — agent starts vsdbg
10. Poll until vsdbg reports ready (max 15 seconds)
11. Construct `VsDebugTargetInfo4` and call `IVsDebugger4.LaunchDebugTargets4()`
12. Register session handle for later cleanup

Cancellation: all steps accept `CancellationToken`. If user presses Stop during launch,
all in-flight operations cancel cleanly. vsdbg process is killed. Port forward is closed.

---

### SshConnectionManager
**File:** `src/PiDbg.Transport/SshConnectionManager.cs`  
**Interface:** `ISshConnectionManager`  
**Lifetime:** Singleton per device (managed by `DeviceConnectionFactory`)

Wraps `SSH.NET.SshClient`. Maintains one open SSH session per device.
Implements exponential-backoff reconnection on disconnect.

Responsibilities:
- Connect/disconnect
- Keepalive (SSH server-side keepalive every 30 seconds)
- Forward port management (creates `ForwardedPortLocal` instances)
- Command execution (`SshCommand`)
- Emits `ConnectionStateChanged` events consumed by VSIX status bar

Thread-safety: `SshClient` is not thread-safe for simultaneous method calls.
All calls are serialized through an internal `SemaphoreSlim(1)`. Port forwarding
once started is handled by SSH.NET background threads — safe to use concurrently after setup.

---

### SftpTransferService
**File:** `src/PiDbg.Transport/SftpTransferService.cs`  
**Interface:** `ISftpTransferService`  
**Lifetime:** Singleton per device (same SSH session as `SshConnectionManager`)

Wraps `SSH.NET.SftpClient`. Note: SFTP client requires a separate connection from SSH
client even though they share credentials — SSH.NET creates a second channel internally.

Progress reporting uses `IProgress<TransferProgress>` where `TransferProgress` carries
bytes transferred, total bytes, current file name, and file index/total count.

For directory uploads, files are transferred sequentially (not in parallel) to avoid
overwhelming the Pi's SSH server. A configurable concurrency limit (default: 3 simultaneous
files) is available for Phase 2.

---

### DeploymentService
**File:** `src/PiDbg.Deployment/DeploymentService.cs`  
**Interface:** `IDeploymentService`  
**Lifetime:** Transient

Orchestrates:
1. `IDeploymentPackager.PackageAsync()` — reads publish output, computes SHA-256 manifest
2. `IAgentClient.BeginDeploymentAsync()` — agent creates staging directory
3. `ISftpTransferService.UploadDirectoryAsync()` — streams files to staging
4. `IAgentClient.CommitDeploymentAsync()` — sends manifest, agent validates and renames

Delta deployment (Phase 2): before step 3, call `AgentClient.ListCurrentDeploymentFilesAsync()`
and compare against manifest. Only transfer files with changed SHA-256.

---

### DeviceRegistry
**File:** `src/PiDbg.DeviceManagement/DeviceRegistry.cs`  
**Interface:** `IDeviceRegistry`  
**Lifetime:** Singleton (VS session lifetime)

Storage: `%LOCALAPPDATA%\PiDbg\devices.json`  
Format: JSON array of `DeviceRecord` (System.Text.Json, source-generated serializer)

Write operations serialize through `SemaphoreSlim(1)` then flush to disk atomically
(write to `.tmp` file, then `File.Move` with overwrite). Events are fired on the VS UI
thread via `IVsUIThreadInvoker`.

`DeviceRecord` is an immutable record:
```csharp
public sealed record DeviceRecord
{
    public required Guid Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Host { get; init; }
    public required int SshPort { get; init; }
    public required string Username { get; init; }
    public required SshAuthMethod AuthMethod { get; init; }
    public string? SshKeyPath { get; init; }          // null if password auth
    public string? DefaultDeployPath { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public DateTimeOffset? LastConnectedAt { get; init; }
    public DeviceCapabilities? LastKnownCapabilities { get; init; }
}
```

---

### DeviceConnectionFactory
**File:** `src/PiDbg.DeviceManagement/DeviceConnectionFactory.cs`  
**Interface:** `IDeviceConnectionFactory`  
**Lifetime:** Singleton

Maintains a `ConcurrentDictionary<Guid, IDeviceConnection>`. On first access, creates
a `DeviceConnection` (SSH session + gRPC channel). On subsequent access, returns the
cached instance after verifying it is still connected. If the cached connection is dead,
disposes it and creates a new one.

gRPC channel construction:
```csharp
var channel = GrpcChannel.ForAddress(
    $"http://localhost:{forwardedPort.BoundPort}",
    new GrpcChannelOptions
    {
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10)
        }
    });
```

---

### AgentClientWrapper
**File:** `src/PiDbg.Vsix/Services/AgentClientWrapper.cs`  
**Interface:** `IAgentClient`  
**Lifetime:** Scoped to `IDeviceConnection`

Wraps the generated gRPC stub (`DebugAgentService.DebugAgentServiceClient`) and translates
`RpcException` into `PiDbgException` subtypes with user-readable messages and `ErrorCode`
enum values for programmatic handling.

All methods set a deadline from the provided `CancellationToken` plus a per-operation
maximum timeout:
- Ping: 5 seconds
- GetStatus: 10 seconds
- Deploy (streaming): no extra deadline (uses CT only)
- StartSession: 30 seconds
- StopSession: 15 seconds

---

### OutputWindowService
**File:** `src/PiDbg.Vsix/Services/OutputWindowService.cs`  
**Lifetime:** Singleton

Acquires the "PiDbg" Output pane from `IVsOutputWindow`. Provides `ILogger`-compatible
sink so Serilog writes to the VS Output window. Log lines include timestamps and level
prefixes. Uses `IVsUIThreadInvoker` to marshal to VS UI thread.

Also buffers last 1000 lines for "show log" command.

---

## Agent-side Services

### AgentGrpcService
**File:** `src/PiDbg.Agent/Services/AgentGrpcService.cs`  
**Implements:** `DebugAgentService.DebugAgentServiceBase` (generated)  
**Lifetime:** Singleton (Kestrel service)

The gRPC server implementation. Each RPC method delegates to an injected service.
Does no logic itself — it is a thin translation layer from gRPC types to domain types.

```
Ping           → direct response
GetStatus      → AgentHealthService
GetVsdbgInfo   → VsdbgManager
InstallVsdbg   → VsdbgManager
BeginDeploy    → DeploymentManager
UploadChunks   → DeploymentManager (streaming)
CommitDeploy   → DeploymentManager
StartSession   → VsdbgManager + ProcessLifecycleService
StopSession    → ProcessLifecycleService
StreamLogs     → ILogger → LogEventChannel → streaming response
```

---

### VsdbgManager
**File:** `src/PiDbg.Agent/Services/VsdbgManager.cs`  
**Interface:** `IVsdbgManager`  
**Lifetime:** Singleton

Knows:
- vsdbg install directory: `/opt/pidbg/vsdbg/`
- vsdbg version file: `/opt/pidbg/vsdbg/.version`
- vsdbg binary: `/opt/pidbg/vsdbg/vsdbg`

Install process:
1. Download vsdbg install script from `https://aka.ms/getvsdbgsh`
2. Execute: `bash getvsdbgsh.sh -v latest -l /opt/pidbg/vsdbg`
3. Verify binary exists and is executable
4. Record version in `.version` file

Note: Download requires internet access on the Pi. If the Pi is air-gapped, the VSIX
bundles vsdbg and uploads it during agent installation (see scripts/provision/install-vsdbg.sh).

Launch: delegates to `IVsdbgLauncher`.

---

### VsdbgLauncher
**File:** `src/PiDbg.Agent/Services/VsdbgLauncher.cs`  
**Interface:** `IVsdbgLauncher`  
**Lifetime:** Singleton

Constructs the vsdbg command line and spawns the process.

Launch mode arguments:
```
/opt/pidbg/vsdbg/vsdbg
  --server
  --port 4024
  --engineLogging=/opt/pidbg/logs/vsdbg-engine.log
  --
  dotnet /opt/pidbg/apps/<id>/current/App.dll [appArgs]
```

Attach mode arguments:
```
/opt/pidbg/vsdbg/vsdbg
  --server
  --port 4024
  --engineLogging=/opt/pidbg/logs/vsdbg-engine.log
  --pid <pid>
```

The `--engineLogging` flag writes the vsdbg internal log. Invaluable for diagnostics.
This path is included in the agent's log stream back to the VSIX.

After launch, polls for port 4024 to become bound (max 10 seconds) before reporting
`SessionStarted` to the VSIX.

---

### ProcessLifecycleService
**File:** `src/PiDbg.Agent/Services/ProcessLifecycleService.cs`  
**Interface:** `IProcessLifecycleService`  
**Lifetime:** Singleton

Wraps `System.Diagnostics.Process`. Tracks active processes in a
`ConcurrentDictionary<int, ProcessHandle>`. Raises `ProcessExited` via background
event loop that watches each tracked process's `WaitForExitAsync()`.

Stop sequence:
1. Send SIGTERM (`Process.Kill(false)` on .NET 10)
2. Wait up to `gracePeriod` (default 2 seconds for app, 1 second for vsdbg)
3. If still running: `Process.Kill(true)` (SIGKILL)

**Stale process cleanup** — called at the start of every `StartDebugSessionAsync`:

`FindAppProcessAsync(appName)` resolves a PID in two passes:
1. Tracked PID from the stored `DebugSessionRecord` — checked via `/proc/<pid>/cmdline`
   to confirm it still matches `/opt/pidbg/apps/<appName>/current/`
2. Scan `/proc/*/cmdline` for any `dotnet` process whose command line contains
   `/opt/pidbg/apps/<appName>/current/` (catches restarts and agent-restart scenarios)

`FindVsdbgProcessAsync(portRange)` resolves orphaned vsdbg PIDs in two passes:
1. Tracked vsdbg PID from `DebugSessionRecord`
2. Scan `/proc/*/net/tcp` (or use `ss -tlnp`) for listeners on ports 4024–4124,
   then confirm the owning process is named `vsdbg`

Both scan methods read `/proc` directly — no shell invocation, no external process.
This keeps the cleanup fast and avoids fork overhead on a resource-constrained Pi.

---

### DeploymentManager
**File:** `src/PiDbg.Agent/Services/DeploymentManager.cs`  
**Interface:** `IDeploymentManager`  
**Lifetime:** Singleton

State machine per deployment ID:
- `Created` → staging directory created
- `Receiving` → chunks being written to staging
- `Verifying` → manifest SHA-256 check
- `Committed` → atomic rename complete
- `Failed` → staging cleaned up

Staging directory: `/opt/pidbg/apps/<deployment-id>/staging/`  
Chunk writes use `FileStream` with `FileOptions.WriteThrough` for durability.

On `CommitDeployment`:
1. Verify all files in manifest exist in staging and SHA-256 matches
2. Delete current directory if it exists (old version discarded)
3. Rename staging → current

---

### MeadowDaemonClient
**File:** `src/PiDbg.Agent/Services/MeadowDaemonClient.cs`  
**Interface:** `IMeadowDaemonClient`  
**Lifetime:** Singleton

`HttpClient` targeting `http://127.0.0.1:5000`. Before any call, checks if Meadow.Daemon
is actually running (via `GET /api/info` with 1-second timeout). If not running, all
methods return gracefully without error — Meadow.Daemon is optional.

Failure policy: if Meadow.Daemon fails to stop the managed process within 10 seconds,
the agent logs a warning and proceeds with the debug session anyway (best-effort coordination).

---

### AgentHealthService
**File:** `src/PiDbg.Agent/Services/AgentHealthService.cs`  
**Lifetime:** Singleton

Reports agent status including:
- Agent version
- OS info (from `/etc/os-release`)
- .NET runtime version
- Disk free space at `/opt/pidbg/`
- Active deployments count
- Active debug sessions count
- vsdbg status
- Meadow.Daemon presence

Used by `Ping` and `GetStatus` RPCs.
