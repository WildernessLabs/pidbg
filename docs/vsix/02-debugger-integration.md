# PiDbg VSIX — Debugger Integration

---

## 1. Strategy

PiDbg does not implement a debugger engine. It is an **orchestration layer** that:
1. Builds and deploys the app to the Pi
2. Arranges vsdbg (Microsoft's .NET Core debugger) on the Pi
3. Tunnels the vsdbg TCP port through SSH to localhost
4. Hands the localhost endpoint to VS's existing managed .NET Core debugger engine

VS's debugger engine (`{2E36F1D4-B23C-435D-AB41-18E608940038}`) handles all protocol
communication with vsdbg. PiDbg has no knowledge of the ICorDebug protocol.

### F5 vs Ctrl+F5

| Trigger | `DebugLaunchOptions` | PiDbg behaviour |
|---|---|---|
| F5 (Debug) | Normal | Deploy → vsdbg → SSH tunnel → attach debugger |
| Ctrl+F5 (Run) | `NoDebug` flag set | Deploy → start app directly (no vsdbg, no tunnel) |
| Rebuild & Redeploy | N/A (command) | Build → deploy only, no launch |

For `NoDebug`, vsdbg is not involved. The agent starts the app directly via `dotnet App.dll`.
The app runs freely on the Pi with no debugger overhead.

---

## 2. VS Debugger API Surface

### IVsDebugger4 — launch
```csharp
// Acquired via: await package.GetServiceAsync(typeof(SVsShellDebugger))
// then cast to IVsDebugger4
void LaunchDebugTargets4(
    uint cTargets,
    VsDebugTargetInfo4[] rgDebugTargetInfo,
    VsDebugTargetProcessInfo[] pLaunchResults);
```

### VsDebugTargetInfo4 — the attach descriptor

```csharp
var target = new VsDebugTargetInfo4
{
    // Path to the managed assembly on the remote machine.
    // Used for symbol loading — must match the deployed DLL path.
    bstrExe = "/opt/pidbg/apps/MyApp/current/MyApp.dll",

    // Working directory on the remote (for relative path resolution).
    bstrCurDir = "/opt/pidbg/apps/MyApp/current",

    // DLO_AlreadyRunning: vsdbg is already running and waiting; VS connects to it.
    dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_AlreadyRunning,

    // The managed .NET Core debug engine.
    guidLaunchDebugEngine = new Guid("2E36F1D4-B23C-435D-AB41-18E608940038"),
    dwDebugEngineCount = 1,
    pDebugEngines = new[] { new Guid("2E36F1D4-B23C-435D-AB41-18E608940038") },

    // Connection parameters passed to the engine.
    // Format confirmed against VS 2022 SSH remote debug implementation.
    bstrOptions = $"transport=tcp;host=127.0.0.1;port={localTunnelPort}",

    LaunchFlags = (uint)(__VSDBGLAUNCHFLAGS.DBGLAUNCH_Silent),
};
```

**Note on `bstrOptions` format**: This format is used by the VS managed code engine for
TCP attach. It must be validated against the VS 2026 SDK. The fields `transport`, `host`,
and `port` are well-established in VS 2022 remote debugging. `targetArchitecture=arm64`
may be needed; confirm during Phase 2 implementation.

### IVsDebuggerEvents — session monitoring
```csharp
// Register to know when the debug session starts/ends:
var debugger = await package.GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger;
debugger.AdviseDebuggerEvents(this, out _cookie);

// IVsDebuggerEvents implementation:
public int OnModeChange(DBGMODE dbgmodeNew)
{
    if (dbgmodeNew == DBGMODE.DBGMODE_Design)
        _package.JoinableTaskFactory.RunAsync(
            () => _orchestrator.OnSessionEndedAsync(_sessionCts.Token));
    return VSConstants.S_OK;
}
```

---

## 3. RaspberryPiDebugLaunchProvider

The core launch provider. MEF-exported via the thin shell in `RaspberryPiLaunchProviderMef`.

### CanLaunchAsync
```csharp
public Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
{
    // Only handle profiles with our commandName.
    // CPS ensures this is only called for "RaspberryPi" profiles,
    // but guard defensively.
    var profile = _launchProfileReader.GetActiveProfile();
    return Task.FromResult(
        profile?.CommandName == RaspberryPiDebugger.CommandName);
}
```

### QueryDebugTargetsAsync
Called by VS to get the list of debug targets. This is where the full sequence runs.

```csharp
public async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
    DebugLaunchOptions launchOptions)
{
    var profile = RaspberryPiLaunchProfile.From(_launchProfileReader.GetActiveProfile());
    var isNoDebug = launchOptions.HasFlag(DebugLaunchOptions.NoDebug);

    // All heavy work on background thread.
    await TaskScheduler.Default;

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(
        _sessionLifetime.Token);
    cts.CancelAfter(TimeSpan.FromMinutes(5)); // absolute session start timeout

    var settings = await _orchestrator.PrepareDebugTargetAsync(
        profile, isNoDebug, cts.Token);

    return new[] { settings };
}
```

`PrepareDebugTargetAsync` drives the full sequence: connect → clean slate → deploy →
vsdbg → tunnel → return `IDebugLaunchSettings`. The returned settings object is what
VS uses to call `LaunchDebugTargets4`.

### LaunchAsync
Called as fallback if `QueryDebugTargetsAsync` returned empty. In our design this should
never happen — we always return a settings object or throw. Implemented as a no-op guard:
```csharp
public Task LaunchAsync(DebugLaunchContext ctx)
    => Task.CompletedTask; // QueryDebugTargetsAsync handles everything
```

---

## 4. DebugSessionOrchestrator

Owns the full F5 sequence. Transient — one instance per press of F5.

### State
```
Idle → Connecting → CleaningUp → Building → Deploying →
LaunchingVsdbg → OpeningTunnel → Attaching → Active → Stopping → Idle
```

Each state transition is logged to the Output window and updates the status bar.

### PrepareDebugTargetAsync (F5 mode)
```
1.  Resolve device from profile.deviceId
2.  Acquire SSH connection (connect if not already)
3.  Ping agent — verify alive
4.  Check agent version — update if needed
5.  Check vsdbg installed — install if needed
6.  [Clean-slate] Kill stale app + vsdbg (see doc 10)
7.  Trigger MSBuild (IBuildManager)
8.  Run dotnet publish
9.  Package manifest (SHA-256)
10. Deploy via SFTP → commit
11. Allocate vsdbg local tunnel port
12. Call agent: StartSession → returns vsdbg remote port
13. Open SSH ForwardedPortLocal (local → Pi:vsdbgPort)
14. Build VsDebugTargetInfo4
15. Return DebugLaunchSettings wrapping VsDebugTargetInfo4
```

VS takes the returned `DebugLaunchSettings` and calls `IVsDebugger4.LaunchDebugTargets4()`.

### PrepareNoDebugTargetAsync (Ctrl+F5 mode)
```
1–10. Same as above
11. Call agent: StartNoDebugSession (app launched without vsdbg)
12. Return DebugLaunchSettings with DLO_CreateProcess pointing to remote app
    (VS shows process as running but no debugger)
```

For `NoDebug`, the `VsDebugTargetInfo4.dlo` is `DLO_CreateProcess` and no engine GUID
is specified. VS starts the app without attaching any debugger.

### OnSessionEndedAsync
Called when `IVsDebuggerEvents.OnModeChange` fires with `DBGMODE_Design`:
```
1. Cancel session CancellationToken
2. Close vsdbg SSH port forward
3. Call agent: StopSession (kills vsdbg + app, optionally resumes Meadow.Daemon)
4. Log session duration to Output window
5. Update status bar to "PiDbg: Ready"
6. Dispose session resources
```

---

## 5. Cancellation Model

Every long-running operation participates in a three-way `CancellationToken` hierarchy:

```
PackageLifetime (fires on VS close)
    └── SessionLifetime (fires on user Stop / VS mode change)
            └── OperationTimeout (fires after per-operation deadline)
```

```csharp
internal sealed class DebugSessionOrchestrator
{
    // Injected: package-lifetime token
    private readonly CancellationToken _packageToken;

    // Created per session
    private CancellationTokenSource? _sessionCts;

    public async Task<IDebugLaunchSettings> PrepareDebugTargetAsync(
        RaspberryPiLaunchProfile profile, bool noDebug, CancellationToken callerToken)
    {
        _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(
            _packageToken, callerToken);

        // Per-step timeouts are linked on top:
        using var deployTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCts.Token);
        deployTimeout.CancelAfter(TimeSpan.FromMinutes(3));

        await _deploymentService.DeployAsync(..., deployTimeout.Token);
        // ...
    }
}
```

Cancellation surfaces to the user in the Output window:
```
[PiDbg] Deploy cancelled by user.
```
and in the status bar:
```
PiDbg: Cancelled
```

No exception dialogs for cancellations — cancellation is normal, not an error.

---

## 6. Debugger Attach Orchestration Detail

After `PrepareDebugTargetAsync` returns the `IDebugLaunchSettings`, VS calls
`LaunchDebugTargets4`. The attach happens inside VS's debugger infrastructure:

```
VS debugger engine
  → connects to localhost:<localTunnelPort>
  → TCP data flows through SSH ForwardedPortLocal
  → arrives at vsdbg on Pi:4024
  → vsdbg performs ICorDebug handshake
  → vs debugger gains control of managed process
  → breakpoints activated, symbols loaded
```

From PiDbg's perspective, after returning the settings object, the debugger is "in VS's
hands." PiDbg only needs to ensure:
- The SSH tunnel stays open (keep the `ForwardedPortLocal` alive)
- The session CTS is not cancelled prematurely
- `OnSessionEndedAsync` cleans up when VS fires `OnModeChange(DBGMODE_Design)`

### Symbol loading
VS loads `.pdb` files for breakpoints. The deployed `publish/` output includes PDB files.
VS needs to know where to find them. Options:

**Option A (recommended)**: Set `bstrRemoteMachine` or source map in the debug options so VS
looks in the deployed path on the Pi. vsdbg serves PDB content over the debug protocol.

**Option B**: Copy PDBs to the local symbol cache after deploy. Simple but duplicates files.

Recommendation: Option A — vsdbg's symbol serving works out of the box when the PDB files
are in the same directory as the DLL (which our deployment ensures).

---

## 7. Rebuild / Redeploy Without Debugging

The Device Manager exposes a "Deploy" button that triggers build + deploy without
launching a debug session:

```csharp
// DeviceManagerViewModel.DeployCommand
public async Task ExecuteDeployAsync(CancellationToken ct)
{
    await TaskScheduler.Default;

    // Reuse the same orchestrator sans debug steps
    await _buildService.BuildAndPublishAsync(project, ct);
    await _deploymentService.DeployAsync(publishDir, connection, options,
        _progressReporter, ct);

    await _outputWindow.WriteLineAsync("[PiDbg] Deployment complete.", ct);
}
```

This is also triggered automatically before F5 (the orchestrator always builds before
deploying — it does not assume the last build is current).

---

## 8. "Attach to Running Process" (Phase 2)

The Device Manager will eventually expose a process list for manual attach (like VS's
built-in "Attach to Process" dialog). The sequence differs from F5:

```
1. Open Device Manager
2. Select device
3. Click "Attach to Process"
4. VSIX calls AgentClient.ListProcessesAsync() → shows dotnet processes
5. User selects process
6. Agent: StartSession(mode=Attach, pid=<selected>)
7. Agent: vsdbg --server --port 4024 --pid <selected>
8. VSIX: open tunnel, call LaunchDebugTargets4 with DLO_AlreadyRunning
```

No build or deploy step — attaching to an existing process.
