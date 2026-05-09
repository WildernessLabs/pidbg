# Meadow.Daemon — Debugger Manager

---

## 1. Responsibilities

The debugger manager owns everything related to vsdbg:
- Install and version-check vsdbg on first use
- Start vsdbg as a TCP server on a free port in the configured range
- Track active debug sessions
- Clean up sessions when vsdbg or the app exits
- Expose session state for status queries

---

## 2. VsdbgManager

```csharp
internal sealed class VsdbgManager
{
    private readonly SemaphoreSlim _installLock = new(1);
    private readonly DaemonOptions _opts;
    private readonly ILogger<VsdbgManager> _log;

    // Returns the vsdbg binary path, installing/updating as needed
    public Task<string> EnsureVsdbgAsync(
        string? requiredVersion, CancellationToken ct);

    // Returns info about installed vsdbg (null if not installed)
    public Task<VsdbgInfo?> GetVsdbgInfoAsync(CancellationToken ct);

    // Installs vsdbg from the official GetVsDbg.sh script
    public Task InstallVsdbgAsync(string version, CancellationToken ct);

    // Installs vsdbg from a pre-uploaded tarball at the given path
    public Task InstallVsdbgFromTarballAsync(string tarballPath, CancellationToken ct);
}
```

### VsdbgInstaller

```csharp
internal sealed class VsdbgInstaller
{
    // Download and install vsdbg at /opt/meadow/vsdbg/{version}/
    public async Task InstallAsync(string version, CancellationToken ct)
    {
        var targetDir = Path.Combine(_opts.VsdbgRoot, version);
        Directory.CreateDirectory(targetDir);

        // Download GetVsDbg.sh from Microsoft
        using var http = new HttpClient();
        var script = await http.GetStringAsync(GetVsDbgShUrl, ct);

        // Execute: bash GetVsDbg.sh -v {version} -l {targetDir}
        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-c", $". /dev/stdin -v {version} -l {targetDir}" },
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        var proc = Process.Start(psi)!;
        await proc.StandardInput.WriteAsync(script);
        proc.StandardInput.Close();
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new VsdbgInstallException($"GetVsDbg.sh exited {proc.ExitCode}");

        // Persist installed version
        await File.WriteAllTextAsync(
            Path.Combine(_opts.VsdbgRoot, "installed-version"), version, ct);
    }
}
```

For air-gapped devices, the VSIX can upload the vsdbg tarball via SFTP and call
`InstallVsdbg` with an `upload_path` (from `UploadVsdbgTarball` gRPC call), bypassing
the download. The daemon extracts the tarball to `vsdbg/{version}/`.

---

## 3. DebugSessionManager

Manages active debug sessions. One session per app is the expected cardinality (the
port range allows multiple, but the VSIX currently requests one at a time).

```csharp
internal sealed class DebugSessionManager
{
    private readonly ConcurrentDictionary<string, DebugSessionRecord>
        _sessions = new();
    private readonly VsdbgManager _vsdbgManager;
    private readonly VsdbgLauncher _launcher;
    private readonly ProcessManager _processManager;
    private readonly StateStore _stateStore;

    // Start a debug session: launch vsdbg, attach to app pid
    public Task<StartSessionResult> StartDebugSessionAsync(
        StartDebugSessionRequest request, CancellationToken ct);

    // Stop a session: kill vsdbg, optionally resume Meadow daemon app
    public Task<StopSessionResult> StopDebugSessionAsync(
        string sessionId, bool resumeMeadowDaemon, CancellationToken ct);

    // Current status of a session
    public Task<SessionStatus> GetSessionStatusAsync(
        string sessionId, CancellationToken ct);

    // All active sessions
    public Task<IReadOnlyList<SessionStatus>> ListSessionsAsync(CancellationToken ct);
}
```

---

## 4. VsdbgLauncher

Spawns vsdbg in TCP server mode and waits for it to bind the port.

```csharp
internal sealed class VsdbgLauncher
{
    public async Task<VsdbgHandle> LaunchAsync(
        VsdbgLaunchOptions options, CancellationToken ct)
    {
        var vsdbgBin = await _vsdbgManager.EnsureVsdbgAsync(options.RequiredVersion, ct);
        var port = await SelectPortAsync(options.PortRangeStart, options.PortRangeEnd, ct);

        // Launch vsdbg in server mode:
        // vsdbg --server --port {port} --interpreter=vscode [--attach {pid}]
        var args = new List<string>
        {
            "--server",
            $"--port={port}",
            "--interpreter=vscode",
        };

        if (options.AttachPid.HasValue)
        {
            args.Add("--attach");
            args.Add(options.AttachPid.Value.ToString());
        }

        var psi = new ProcessStartInfo(vsdbgBin)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        var proc = Process.Start(psi)!;
        _log.LogInformation("Launched vsdbg (PID {Pid}) on port {Port}", proc.Id, port);

        // Wait for vsdbg to bind the port (poll every 250ms, timeout 10s)
        await WaitForPortAsync(port, TimeSpan.FromSeconds(10), ct);

        return new VsdbgHandle(proc, port);
    }

    private async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        while (true)
        {
            cts.Token.ThrowIfCancellationRequested();

            if (IsPortListening(port)) return;

            await Task.Delay(250, cts.Token);
        }
    }

    private static bool IsPortListening(int port)
    {
        // Read /proc/net/tcp6 (or tcp) — faster than creating a socket probe
        try
        {
            foreach (var line in File.ReadLines("/proc/net/tcp6").Skip(1))
            {
                var parts = line.TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                // local_address field is hex "00000000000000000000000001000000:0FB8" (port 4024)
                var hexPort = parts[1].Split(':')[^1];
                if (Convert.ToInt32(hexPort, 16) == port &&
                    parts[3] == "0A") // 0A = LISTEN
                    return true;
            }
        }
        catch { /* /proc/net/tcp6 read failed */ }
        return false;
    }
}
```

---

## 5. Port Selection

```csharp
private async Task<int> SelectPortAsync(
    int rangeStart, int rangeEnd, CancellationToken ct)
{
    // Build set of ports already in use by active sessions
    var usedPorts = _sessions.Values
        .Where(s => s.State == SessionState.Active)
        .Select(s => s.VsdbgPort)
        .ToHashSet();

    for (var port = rangeStart; port <= rangeEnd; port++)
    {
        if (usedPorts.Contains(port)) continue;
        if (!IsPortInUse(port)) return port;
    }

    throw new NoAvailablePortException(
        $"No free vsdbg port in range {rangeStart}-{rangeEnd}");
}

private static bool IsPortInUse(int port)
{
    // Quick socket probe — faster than parsing /proc/net/tcp for all ports
    try
    {
        using var sock = new System.Net.Sockets.TcpClient();
        sock.Connect("127.0.0.1", port);
        return true; // connected → port in use
    }
    catch { return false; }
}
```

---

## 6. StartDebugSession Flow

```
StartDebugSessionAsync(request)
  │
  ├── Validate: appName exists in ProcessManager
  ├── Validate: no active session for this app (or allow override via force=true)
  │
  ├── EnsureVsdbgAsync(request.RequiredVersion)
  │     └── If not installed: InstallVsdbgAsync (blocks, shows progress via log stream)
  │
  ├── If request.Mode == Attach:
  │     ├── GetAppPidAsync(request.AppName)  ← from ProcessManager
  │     └── VsdbgLauncher.LaunchAsync(options with AttachPid)
  │
  ├── If request.Mode == Launch:
  │     ├── BuildLaunchJsonAsync(request)   ← constructs vsdbg launch.json params
  │     └── VsdbgLauncher.LaunchAsync(options without AttachPid)
  │         Note: vsdbg reads the launch JSON via the debug adapter protocol,
  │               not via command-line args.
  │
  ├── sessionId = NewSessionId()
  ├── record = new DebugSessionRecord { sessionId, appName, vsdbgPid, vsdbgPort, ... }
  ├── _sessions[sessionId] = record
  ├── StateStore.WriteSessionsAsync(AllSessions)
  │
  └── Return StartSessionResult(sessionId, vsdbgPid, vsdbgPort)
```

---

## 7. Session Lifecycle Events

### vsdbg exits unexpectedly

`DebugSessionManager` subscribes to `ProcessExited` for the vsdbg PID:

```csharp
vsdbgProcess.Exited += (_, _) =>
{
    _log.LogWarning("vsdbg (PID {Pid}, session {Id}) exited unexpectedly",
        handle.Pid, sessionId);
    CleanupSessionAsync(sessionId, CancellationToken.None).FireAndForget();
};
```

### App exits during debug session

`DebugSessionManager` subscribes to `ProcessManager.ProcessExited`:

```csharp
_processManager.ProcessExited += (_, args) =>
{
    var session = _sessions.Values
        .FirstOrDefault(s => s.AppName == args.AppName);
    if (session is null) return;

    _log.LogInformation(
        "App {App} exited (code {Code}) — ending session {Id}",
        args.AppName, args.ExitCode, session.SessionId);
    CleanupSessionAsync(session.SessionId, CancellationToken.None).FireAndForget();
};
```

`CleanupSessionAsync` removes the record from `_sessions`, updates `sessions.json`,
and if vsdbg is still alive, sends SIGTERM to it.

---

## 8. StopDebugSession Flow

```
StopDebugSessionAsync(sessionId, resumeMeadowDaemon)
  │
  ├── Lookup session record
  ├── Kill vsdbg: SIGTERM → 3s → SIGKILL
  ├── Remove from _sessions
  ├── StateStore.WriteSessionsAsync(AllSessions)
  │
  └── If resumeMeadowDaemon && session.AppName is Meadow-managed:
        ProcessManager.StartApplicationAsync(session.AppName, useDebugSlot=false)
        Note: starts production slot — not debug slot
```

`resumeMeadowDaemon` is the VSIX-side `resumeMeadowDaemon` profile setting. When true,
the daemon restarts the app in production mode after the debug session ends, giving the
device back to normal Meadow operation.

---

## 9. DebugSessionRecord

```csharp
internal sealed class DebugSessionRecord
{
    public string SessionId    { get; init; } = "";
    public string AppName      { get; init; } = "";
    public int    VsdbgPid     { get; init; }
    public int    VsdbgPort    { get; init; }
    public int?   AppPid       { get; init; }
    public SessionMode Mode    { get; init; }
    public SessionState State  { get; set; } = SessionState.Starting;
    public DateTimeOffset StartedAt { get; init; }
    public string CorrelationId { get; init; } = "";  // 8-char hex from VSIX session
}
```

`CorrelationId` matches the VSIX-side session ID so logs from both ends can be joined
during troubleshooting.

---

## 10. Reconnect Support

If the VSIX loses its SSH tunnel mid-session (network blip, laptop sleep/wake), it can
reconnect by:

1. Calling `GetSessionStatus(sessionId)` — session is still `Active` if vsdbg is still running
2. Re-establishing the SSH tunnel for the vsdbg port
3. Calling `IVsDebugger4.LaunchDebugTargets4` with `dlo = DLO_AlreadyRunning` to re-attach
   the VS debugger to the existing vsdbg session

The daemon does not terminate the vsdbg process on SSH disconnect — it only terminates
on explicit `StopDebugSession`, vsdbg self-exit, or app exit. This is the reconnect window.

### Reconnect timeout

Configurable via `DebugSessionOrphanTimeoutMinutes` (default: 30 minutes). If a session
has had no gRPC activity for this duration, `ProcessMonitorService` treats it as orphaned
and calls `StopDebugSessionAsync`. This prevents zombie vsdbg processes after a developer
closes VS without cleanly stopping.

```csharp
// In ProcessMonitorService.CheckTrackedProcessesAsync:
foreach (var session in _debugSessionManager.GetActiveSessions())
{
    var idleFor = DateTimeOffset.UtcNow - session.LastActivityAt;
    if (idleFor > _opts.DebugSessionOrphanTimeout)
    {
        _log.LogWarning("Orphaning idle session {Id} (idle {Min}min)",
            session.SessionId, (int)idleFor.TotalMinutes);
        await _debugSessionManager.StopDebugSessionAsync(
            session.SessionId, resumeMeadowDaemon: false, ct);
    }
}
```

`LastActivityAt` is updated on every `GetSessionStatus` call from the VSIX heartbeat
(called every 30 seconds during an active session).
