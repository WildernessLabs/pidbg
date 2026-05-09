# PiDbg — Debugger Attach Flow

---

## 1. Overview

After deployment, the VSIX must connect Visual Studio's debugger engine to the application
running on the Pi. This is achieved by:

1. Launching vsdbg on the Pi in TCP server mode
2. Creating an SSH port forward from a local ephemeral port to vsdbg's TCP port on the Pi
3. Instructing VS's debugger engine to attach to `localhost:<local port>`
4. VS debugger connects through the tunnel, negotiates with vsdbg, and takes control of the app

The VS debugger engine is the standard managed .NET Core engine — PiDbg does not implement
any debug protocol. vsdbg implements the ICorDebug protocol on the Pi side.

---

## 2. vsdbg Installation

vsdbg must be installed before the first debug session. The VSIX checks on every launch.

### Version Management
vsdbg versioning tracks Visual Studio versions. VS 2026 requires vsdbg >= 17.x.
The VSIX bundles the expected vsdbg version string. On first launch:

```
[PiDbg] Checking vsdbg on raspberrypi...
[PiDbg] vsdbg not installed. Installing vsdbg 17.x for ARM64...
[PiDbg] Downloading install script...
[PiDbg] Running: bash /tmp/getvsdbgsh.sh -v 17.* -l /opt/pidbg/vsdbg
[PiDbg] vsdbg installed: /opt/pidbg/vsdbg/vsdbg (version 17.0.xxxx)
```

### Installation via SSH command
```csharp
// Agent receives InstallVsdbg gRPC call
// Downloads script and runs locally on Pi
await _sshConnection.ExecuteCommandAsync(
    "curl -sSL https://aka.ms/getvsdbgsh | bash /dev/stdin -v 17.* -l /opt/pidbg/vsdbg",
    cancellationToken);
```

For air-gapped environments: VSIX bundles vsdbg ARM64 tarball and uploads via SFTP
before calling `AgentClient.InstallVsdbgFromUploadAsync()`. The agent extracts it locally.

### Version Verification
After install, agent verifies:
```bash
/opt/pidbg/vsdbg/vsdbg --version
```
Response format: `Microsoft (R) Visual Studio Code Debugger Engine. Version 17.0.xxxx`

The agent stores the version in `/opt/pidbg/vsdbg/.version` for fast subsequent checks.

---

## 3. Pre-Attach Sequence

The agent applies a **clean-slate policy** at the start of every session. The developer
pressed F5 — they want a fresh debug session, not a negotiation with whatever is already
running. Every stale process is killed, unconditionally, before proceeding.

### Step 1: Kill stale app process

The agent checks for an existing instance of the target app in two passes:

**Pass A — tracked PID** (fast path): if the agent holds a session record from a previous
F5 (app PID stored in `DebugSessionRecord`), check whether that process is still alive.

**Pass B — process scan** (fallback): if no tracked PID, or if the tracked PID no longer
matches the expected binary, scan running `dotnet` processes whose command line contains
`/opt/pidbg/apps/<appName>/current/`. This catches the case where the agent was restarted
and lost its session state, or where the process was re-launched outside of PiDbg.

Kill sequence for any found process:
```csharp
await _processLifecycle.StopProcessAsync(pid, gracePeriod: TimeSpan.FromSeconds(2), ct);
// StopProcessAsync: SIGTERM → wait gracePeriod → SIGKILL if still alive
_logger.LogInformation("Stopped existing {AppName} process (PID {Pid})", appName, pid);
```

Output window:
```
[PiDbg] Found existing MyApp process (PID 4829) — stopping
```

If the process cannot be killed (e.g., stuck in uninterruptible sleep, kernel bug), the
agent logs an error and returns `FailedPrecondition` to the VSIX. The VSIX surfaces:
_"Could not stop existing MyApp process (PID 4829). Try rebooting the Pi."_
This is the only case where the clean-slate policy cannot proceed.

### Step 2: Kill orphaned vsdbg

Same two-pass approach for vsdbg:

**Pass A — tracked PID**: check the vsdbg PID from the previous session record.

**Pass B — port scan**: scan for any process listening on ports 4024–4124 (the vsdbg
range). A `vsdbg` process listening on one of these ports is always safe to kill — nothing
else uses this range.

```csharp
await _processLifecycle.StopProcessAsync(vsdbgPid, gracePeriod: TimeSpan.FromSeconds(1), ct);
_logger.LogInformation("Stopped orphaned vsdbg (PID {Pid}) on port {Port}", vsdbgPid, port);
```

Output window:
```
[PiDbg] Found orphaned vsdbg (PID 4823) on port 4024 — stopping
```

### Step 3: Settle delay

After all kills, wait 500 ms before proceeding. This gives the OS time to release TCP
port bindings. Without this, vsdbg can fail to bind if the previous vsdbg's `TIME_WAIT`
state hasn't fully cleared.

```csharp
await Task.Delay(500, ct);
```

### Step 4: Coordinate with Meadow.Daemon
```csharp
var meadowRunning = await _meadowClient.IsDaemonRunningAsync(ct);
if (meadowRunning)
{
    var process = await _meadowClient.GetManagedProcessInfoAsync(ct);
    if (process != null && process.AppName == targetAppName)
    {
        _logger.LogInformation("Requesting Meadow.Daemon to stop managed process {App}", process.AppName);
        var stopped = await _meadowClient.RequestProcessStopAsync(ct);
        if (!stopped)
            _logger.LogWarning("Meadow.Daemon did not stop process; proceeding anyway");
    }
}
```

This is best-effort. If Meadow.Daemon is not running, or if the app is not Meadow-managed,
this step is skipped silently. Note that Step 1 will already have killed the app process
even if Meadow.Daemon managed it — this step just notifies Meadow.Daemon so it does not
attempt to restart the process during the debug session.

### Step 5: Select vsdbg port
Agent selects a port from range 4024–4124. Port 4024 is preferred (vsdbg default).
Port is communicated back to VSIX in `StartSessionResponse`.

### Step 6: Launch vsdbg (Launch mode)
```bash
# Command executed by VsdbgLauncher
/opt/pidbg/vsdbg/vsdbg \
  --server \
  --port 4024 \
  --engineLogging=/opt/pidbg/logs/vsdbg-engine-{sessionId}.log \
  -- \
  dotnet /opt/pidbg/apps/MyApp/current/MyApp.dll [args]
```

Environment variables passed through from launch profile:
- `DOTNET_ENVIRONMENT=Development`
- Any user-defined vars from the launch profile
- `VSDBG_SUPPRESS_MULTIPROCESS_WARNING=1` (suppresses noise)

### Step 6 (Attach mode — alternative)
When attaching to an already-running process:
```bash
/opt/pidbg/vsdbg/vsdbg \
  --server \
  --port 4024 \
  --engineLogging=/opt/pidbg/logs/vsdbg-engine-{sessionId}.log \
  --pid 12345
```

The PID is obtained from `AgentClient.GetSessionStatusAsync()` which scans running
dotnet processes matching the deployed app's DLL name.

### Step 7: Wait for vsdbg to bind port
Agent polls `ss -tlnp` (or equivalent socket probe) every 250ms, up to 10 seconds:
```csharp
var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
while (DateTime.UtcNow < deadline)
{
    if (IsPortBound(127, 0, 0, 1, vsdbgPort))
    {
        _logger.LogInformation("vsdbg bound on port {Port}", vsdbgPort);
        return vsdbgPort;
    }
    await Task.Delay(250, ct);
}
throw new VsdbgStartTimeoutException($"vsdbg did not bind port {vsdbgPort} within 10 seconds");
```

### Step 8: Report session ready to VSIX
```protobuf
message StartSessionResponse {
  string session_id = 1;
  int32 vsdbg_port = 2;       // Remote port (127.0.0.1 only on Pi)
  int32 vsdbg_pid = 3;
  google.protobuf.Timestamp started_at = 4;
}
```

---

## 4. SSH Port Forward Setup

After receiving `StartSessionResponse.VsdbgPort`:

```csharp
// VSIX side
int localPort = AllocateEphemeralPort();
var forward = await _connectionManager.AddLocalForwardAsync(
    remotePort: response.VsdbgPort,  // e.g., 4024
    cancellationToken);
// forward.BoundPort = localPort (the actual bound local port)

_logger.LogDebug("vsdbg tunnel: localhost:{LocalPort} → Pi:{RemotePort}",
    forward.BoundPort, response.VsdbgPort);
```

The forward is registered in the `DebugSessionHandle` and closed when the session ends.

---

## 5. VS Debugger Attach

This is the most VS-integration-specific step. The VSIX builds a `VsDebugTargetInfo4`
and submits it to the VS debugger via `IVsDebugger4.LaunchDebugTargets4()`.

```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

var debugger4 = (IVsDebugger4)await _package.GetServiceAsync(typeof(SVsShellDebugger));

var targetInfo = new VsDebugTargetInfo4
{
    dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_AlreadyRunning,
    bstrExe = remoteAppPath,    // "/opt/pidbg/apps/MyApp/current/MyApp.dll"
    bstrCurDir = remoteWorkDir, // "/opt/pidbg/apps/MyApp/current/"
    guidLaunchDebugEngine = ManagedCoreDebugEngineGuid,
    // Engine GUID: {2E36F1D4-B23C-435D-AB41-18E608940038}
    dwDebugEngineCount = 1,
    pDebugEngines = new[] { ManagedCoreDebugEngineGuid },
    LaunchFlags = (uint)(DEBUG_LAUNCH_FLAGS.DLF_NONE),
    bstrOptions = BuildDebugOptions(forward.BoundPort, session.SessionId),
};

VsDebugTargetProcessInfo[] processInfo = new VsDebugTargetProcessInfo[1];
debugger4.LaunchDebugTargets4(1, new[] { targetInfo }, processInfo);
```

### Debug options format
The `bstrOptions` string for .NET Core TCP remote attach:
```
transport=tcp;host=127.0.0.1;port=<localForwardPort>;targetArchitecture=arm64
```

This is passed through the managed debug engine to establish the connection to vsdbg.

**Note:** The exact `bstrOptions` format and `guidLaunchDebugEngine` GUID must be validated
against VS 2026 SDK during implementation. These values are correct for VS 2022 / 17.x and
are expected to remain stable in VS 2026 / 18.x but are subject to confirmation.

---

## 6. Debugging Features Enabled

Once attached, the following work via VS's standard debugger UI:

| Feature | How it works |
|---------|-------------|
| Breakpoints | VS debugger sets breakpoints on vsdbg via ICorDebug protocol |
| Single-step (F10/F11) | Standard ICorDebug step commands |
| Step out (Shift+F11) | Standard ICorDebug step-out |
| Watch window | Expression evaluation via vsdbg's expression evaluator |
| Locals window | ICorDebug local variable inspection |
| Call stack | ICorDebug call stack frames |
| Autos window | Standard debug engine feature |
| Immediate window | vsdbg expression evaluation |
| Conditional breakpoints | VS debug engine feature (evaluated locally against vsdbg) |
| Exception settings | VS debug engine → vsdbg exception handling |
| Edit and Continue | **Not supported** (remote debugging limitation) |
| Hot Reload | **Not supported** in Phase 1 (requires additional infrastructure) |
| Async debugging | Supported — .NET 10 async debugging via ICorDebug |
| Tasks window | Supported (async tasks via ICorDebug parallel extensions) |
| DataTips (hover) | Supported |
| Diagnostic tools | **Not supported** remotely (CPU/memory profiler requires different attach) |

---

## 7. Session Teardown

When the user presses Stop (or the app exits):

### VSIX receives stop notification
VS fires `IVsDebuggerEvents.OnModeChange(DBGMODE.DBGMODE_Design)`.
The VSIX `DebugSessionOrchestrator` subscribes to this and begins teardown.

### Teardown sequence
```
1. Cancel session CancellationToken
2. Close SSH port forward for vsdbg tunnel (BoundPort released)
3. Call AgentClient.StopDebugSessionAsync(sessionId)
4. Agent: if vsdbg still running, send SIGTERM (5s grace), then SIGKILL
5. Agent: if app still running under vsdbg, it will exit with vsdbg
6. Agent: optionally notify Meadow.Daemon to resume management
7. VSIX: log "Debug session ended" to Output window
8. VSIX: update status bar
```

### Meadow.Daemon handback (optional)
If Meadow.Daemon was stopped before the session, the agent can restart it:
```csharp
if (_meadowHandoff.WasStoppedByUs)
{
    await _meadowClient.RequestProcessResumeAsync(CancellationToken.None);
    _logger.LogInformation("Notified Meadow.Daemon to resume process management");
}
```

This is configurable per launch profile: "Resume managed process after debug session" (default: true).

---

## 8. Attach to Running Process (Manual Attach)

In addition to F5 launch, users can attach to an already-running process via Device Manager.

Sequence:
1. User opens Device Manager tool window
2. User selects device
3. VSIX calls `AgentClient.ListProcessesAsync()` — returns running dotnet processes
4. User selects process (similar to VS's "Attach to Process" dialog)
5. Same vsdbg launch sequence as above, but with `--pid <pid>` instead of `-- dotnet App.dll`
6. VSIX attaches debugger as above

This is a Phase 2 feature. Phase 1 supports F5 launch only.
