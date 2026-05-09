# Phase 5 — Debugger Integration

Connects the deployment pipeline to a live debug session: the daemon manages the app
process and vsdbg, and the VSIX connects Visual Studio's managed debug engine through an
SSH tunnel to the running vsdbg instance.

Task order:
```
P5.1 (ProcessManager) ──────────────────────────▶ P5.4 (Process RPCs)
P5.2 (OutputBroadcaster) ────────────────────────▶ P5.4
P5.3 (ProcessMonitorService) ────────────────────▶ P5.7 (SessionManager)
P5.5 (VsdbgInstaller) ──────────────────────────▶ P5.6 (VsdbgLauncher)
P5.6 (VsdbgLauncher) ───────────────────────────▶ P5.7
P5.7 (DebugSessionManager) ─────────────────────▶ P5.8 (Session RPCs)
P5.8 (Session RPCs) + P5.10 (Tunnel) ───────────▶ P5.9 (VSIX Launch Provider)
```

---

## P5.1 — ProcessManager

**Purpose**: Start, stop, and monitor the managed application process, providing clean
lifecycle control and capturing stdout/stderr for streaming to clients.

**Dependencies**: P1.5, P1.6, P1.8, P1.10, P5.2

**Files**:
- `Source/Meadow.Daemon/Services/ProcessManager.cs`
- `Source/Meadow.Daemon/Services/IProcessManager.cs`

**Implementation details**:

```csharp
public interface IProcessManager
{
    Task<StartProcessResult> StartAsync(string appName, CancellationToken ct);
    Task StopAsync(string appName, CancellationToken ct);
    Task<StartProcessResult> RestartAsync(string appName, CancellationToken ct);
    AppState GetState(string appName);
    int? GetPid(string appName);
    ProcessOutputBroadcaster GetOutputBroadcaster(string appName);
}

public record StartProcessResult(bool Success, int? Pid, string? Error);
```

```csharp
public sealed class ProcessManager : IProcessManager, IDisposable
{
    // Per-app state: process handle + broadcaster
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _options;
    private readonly ILogger<ProcessManager> _logger;

    private sealed class ManagedProcess
    {
        public Process?                 Handle       { get; set; }
        public ProcessOutputBroadcaster Broadcaster  { get; } = new();
        public AppState                 State        { get; set; } = AppState.Stopped;
        public int                      RestartCount { get; set; }
        public DateTimeOffset           LastCrashAt  { get; set; }
    }

    public async Task<StartProcessResult> StartAsync(string appName, CancellationToken ct)
    {
        var apps = await _stateStore.LoadAppsAsync(ct);
        var app  = apps.Apps.FirstOrDefault(a => a.Name == appName);
        if (app is null)
            return new StartProcessResult(false, null, $"App '{appName}' not registered");

        var managed = _processes.GetOrAdd(appName, _ => new ManagedProcess());
        managed.State = AppState.Starting;

        var entryPoint = Path.Combine(DaemonPaths.AppDebugDir(_options, appName), app.EntryPoint);
        var info = new ProcessStartInfo("dotnet", entryPoint)
        {
            WorkingDirectory       = DaemonPaths.AppDebugDir(_options, appName),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        // Merge custom env vars
        foreach (var kv in app.EnvironmentVariables)
            info.Environment[kv.Key] = kv.Value;

        // Append startup args
        if (app.StartupArgs?.Length > 0)
            foreach (var arg in app.StartupArgs)
                info.ArgumentList.Add(arg);

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                managed.Broadcaster.TryWrite(new OutputLine
                {
                    Stream    = OutputStream.Stdout,
                    Text      = e.Data,
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                });
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                managed.Broadcaster.TryWrite(new OutputLine
                {
                    Stream    = OutputStream.Stderr,
                    Text      = e.Data,
                    Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                });
        };
        process.Exited += (_, _) => OnProcessExited(appName, managed);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        managed.Handle = process;
        managed.State  = AppState.Running;

        // Persist PID to state store
        app = app with { Pid = process.Id, LastStartedAt = DateTimeOffset.UtcNow };
        apps = new AppsState { Apps = apps.Apps.Select(a => a.Name == appName ? app : a).ToList() };
        await _stateStore.SaveAppsAsync(apps, ct);

        _logger.LogInformation("Started app {App} PID={Pid}", appName, process.Id);
        return new StartProcessResult(true, process.Id, null);
    }

    public async Task StopAsync(string appName, CancellationToken ct)
    {
        if (!_processes.TryGetValue(appName, out var managed) || managed.Handle is null)
            return;
        managed.State = AppState.Stopping;
        try
        {
            // SIGTERM first
            managed.Handle.Kill(entireProcessTree: false);
            // Wait for graceful exit
            if (!await managed.Handle.WaitForExitAsync(ct)
                    .WaitAsync(_options.ProcessGracefulStopTimeout, ct))
            {
                // Forceful kill after timeout
                managed.Handle.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        managed.State = AppState.Stopped;
    }

    private void OnProcessExited(string appName, ManagedProcess managed)
    {
        managed.State = AppState.Stopped;
        _logger.LogInformation("App {App} exited with code {Code}",
            appName, managed.Handle?.ExitCode);
        // ProcessMonitorService handles restart logic
    }
}
```

**Edge cases**:
- `Process.Kill()` on Linux sends SIGKILL, not SIGTERM. For graceful shutdown,
  use `Mono.Unix.Native.Syscall.kill(pid, Mono.Unix.Native.Signum.SIGTERM)`.
  Then wait for exit; if timeout, call `Process.Kill()` for SIGKILL.
- `EnableRaisingEvents = true` is required for `Exited` event. Omitting it silently
  disables the event.
- `BeginOutputReadLine` and `BeginErrorReadLine` must be called after `Start()`.
  Calling them before `Start()` throws.
- The `Exited` event fires on a thread pool thread. The handler must be thread-safe.
- `dotnet {entryPoint}` requires .NET 10 runtime on PATH on the Pi. The daemon itself
  is self-contained but the managed app is framework-dependent.

**Testing requirements**:
- Integration test: start a simple .NET console app, verify PID is returned
- Integration test: app stdout is captured and sent to broadcaster
- Integration test: stop sends SIGTERM, app exits cleanly
- Integration test: stop timeout → SIGKILL sent
- Unit test: `OnProcessExited` sets state to `Stopped`

**Definition of done**:
- [ ] `StartAsync` launches `dotnet {entryPoint}` with working dir, env vars, args
- [ ] stdout/stderr captured and forwarded to `ProcessOutputBroadcaster`
- [ ] `StopAsync` sends SIGTERM, waits `ProcessGracefulStopTimeout`, then SIGKILL
- [ ] PID persisted to `StateStore` on start
- [ ] `AppState` transitions: `Stopped → Starting → Running → Stopping → Stopped`
- [ ] All tests pass

---

## P5.2 — ProcessOutputBroadcaster

**Purpose**: Fan out the stdout/stderr of a managed app process to all active
`StreamOutput` gRPC subscribers without blocking the process output pipeline.

**Dependencies**: P1.4, P1.3

**Files**:
- `Source/Meadow.Daemon/Services/ProcessOutputBroadcaster.cs`

**Implementation details**:

```csharp
public sealed class ProcessOutputBroadcaster : IDisposable
{
    // Bounded: if no subscriber reads fast enough, oldest lines are dropped.
    private readonly Channel<OutputLine> _channel =
        Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(2000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,   // only ProcessManager writes
            SingleReader = false,  // multiple StreamOutput subscribers read
        });

    private readonly List<Channel<OutputLine>> _subscribers = new();
    private readonly SemaphoreSlim _subLock = new(1, 1);

    // Called by ProcessManager on each output line
    public bool TryWrite(OutputLine line)
    {
        // Write to primary channel
        _channel.Writer.TryWrite(line);
        // Fan out to all subscriber channels
        // Subscriber writes are non-blocking: slow subscribers drop items
        foreach (var sub in GetSubscribersSnapshot())
            sub.Writer.TryWrite(line);
        return true;
    }

    // Each gRPC StreamOutput call gets its own channel cursor
    public IAsyncEnumerable<OutputLine> Subscribe(CancellationToken ct)
    {
        var sub = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        AddSubscriber(sub);
        return WrapSubscriber(sub, ct);
    }

    private async IAsyncEnumerable<OutputLine> WrapSubscriber(
        Channel<OutputLine> sub,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var line in sub.Reader.ReadAllAsync(ct))
                yield return line;
        }
        finally
        {
            RemoveSubscriber(sub);
        }
    }

    private async Task AddSubscriber(Channel<OutputLine> sub)
    {
        await _subLock.WaitAsync();
        try { _subscribers.Add(sub); }
        finally { _subLock.Release(); }
    }

    private void RemoveSubscriber(Channel<OutputLine> sub)
    {
        _subLock.Wait();
        try { _subscribers.Remove(sub); }
        finally { _subLock.Release(); }
    }

    private IReadOnlyList<Channel<OutputLine>> GetSubscribersSnapshot()
    {
        _subLock.Wait();
        try { return _subscribers.ToList(); }
        finally { _subLock.Release(); }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _subLock.Wait();
        try
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
        }
        finally { _subLock.Release(); }
    }
}
```

**Edge cases**:
- `GetSubscribersSnapshot()` uses `_subLock.Wait()` (synchronous) from the process
  output handler which runs on a thread pool thread. Using `.Wait()` on a
  `SemaphoreSlim` is acceptable here because the critical section is very short
  (list copy only). Do not use `await` inside `TryWrite` — it would require making
  `TryWrite` async which breaks the synchronous `OutputDataReceived` callback.
- Per-subscriber channels have capacity 500. If a single subscriber is slow and drops
  items, other subscribers are unaffected (each has its own channel).
- When the process exits, `_channel.Writer.TryComplete()` and subscriber completion
  signal end of stream to all readers.

**Testing requirements**:
- Unit test: two concurrent subscribers both receive the same lines
- Unit test: slow subscriber drops lines but fast subscriber receives all
- Unit test: after `Dispose()`, `Subscribe()` returns an immediately-ended enumerable
- Unit test: 2001 lines written → oldest 1 line dropped (bounded at 2000)

**Definition of done**:
- [ ] Per-subscriber `Channel<OutputLine>` with capacity 500
- [ ] `TryWrite` fans out to all subscribers without blocking
- [ ] Subscriber removal on `CancellationToken` cancellation
- [ ] `Dispose` completes all subscriber channels
- [ ] Unit tests pass

---

## P5.3 — ProcessMonitorService

**Purpose**: Background service that periodically checks all managed app processes,
reconciles PIDs on startup, detects crashes, triggers auto-restart with crash-loop
protection, and expires idle debug sessions.

**Dependencies**: P5.1, P1.8, P1.5

**Files**:
- `Source/Meadow.Daemon/Services/ProcessMonitorService.cs`

**Implementation details**:

```csharp
public sealed class ProcessMonitorService : BackgroundService
{
    private readonly IProcessManager _processManager;
    private readonly IDebugSessionManager _sessionManager;  // Phase 5.7
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _options;
    private readonly ILogger<ProcessMonitorService> _logger;

    // Crash loop detection: per-app ring buffer of recent restart timestamps
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _restartHistory = new();
    private const int CrashLoopThreshold  = 5;
    private static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup reconciliation: match StateStore PIDs against /proc
        await ReconcileProcessesAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await MonitorCycleAsync(stoppingToken);
        }
    }

    private async Task ReconcileProcessesAsync(CancellationToken ct)
    {
        // For each app with a saved PID, verify the process is still running
        // and is actually the right process (cmdline matches)
        var state = await _stateStore.LoadAppsAsync(ct);
        foreach (var app in state.Apps.Where(a => a.Pid.HasValue))
        {
            var pid = app.Pid!.Value;
            if (IsProcessAlive(pid) && GetProcessCmdline(pid).Contains(app.EntryPoint))
            {
                _processManager.ReconcileRunningProcess(app.Name, pid);
                _logger.LogInformation("Reconciled running app {App} PID={Pid}", app.Name, pid);
            }
            else
            {
                _logger.LogWarning("App {App} PID={Pid} not found; clearing stale PID", app.Name, pid);
                // Clear stale PID in state store
                app.Pid = null;
            }
        }
        if (state.Apps.Any(a => a.Pid == null))
            await _stateStore.SaveAppsAsync(state, ct);
    }

    private async Task MonitorCycleAsync(CancellationToken ct)
    {
        var state = await _stateStore.LoadAppsAsync(ct);
        foreach (var app in state.Apps)
        {
            var currentState = _processManager.GetState(app.Name);
            if (currentState == AppState.Running) continue;
            if (currentState != AppState.Stopped && currentState != AppState.Failed) continue;

            if (!app.AutoStart) continue;
            if (IsInCrashLoop(app.Name)) continue;

            _logger.LogWarning("App {App} is {State}; attempting restart", app.Name, currentState);
            RecordRestart(app.Name);
            await _processManager.StartAsync(app.Name, ct);
        }

        // Expire orphaned debug sessions
        await ExpireOrphanSessionsAsync(ct);
    }

    private bool IsInCrashLoop(string appName)
    {
        var history = _restartHistory.GetOrAdd(appName, _ => new Queue<DateTimeOffset>());
        var cutoff  = DateTimeOffset.UtcNow - CrashLoopWindow;
        while (history.TryPeek(out var oldest) && oldest < cutoff)
            history.Dequeue();
        if (history.Count < CrashLoopThreshold) return false;

        _logger.LogError("Crash loop detected for {App}: {Count} restarts in {Window}",
            appName, history.Count, CrashLoopWindow);
        return true;
    }

    private void RecordRestart(string appName)
    {
        var history = _restartHistory.GetOrAdd(appName, _ => new Queue<DateTimeOffset>());
        history.Enqueue(DateTimeOffset.UtcNow);
    }

    private static bool IsProcessAlive(int pid)
        => Directory.Exists($"/proc/{pid}");

    private static string GetProcessCmdline(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '); }
        catch { return ""; }
    }

    private async Task ExpireOrphanSessionsAsync(CancellationToken ct)
    {
        var sessions = await _stateStore.LoadSessionsAsync(ct);
        var now      = DateTimeOffset.UtcNow;
        var timeout  = _options.DebugSessionOrphanTimeout;
        var expired  = sessions.Sessions
            .Where(s => s.State == SessionState.Ready
                     && now - s.LastActivityAt > timeout)
            .ToList();

        foreach (var session in expired)
        {
            _logger.LogWarning("Debug session {Id} orphaned (idle {Minutes}m); stopping",
                session.SessionId, (int)(now - session.LastActivityAt).TotalMinutes);
            await _sessionManager.StopDebugSessionAsync(session.SessionId, ct);
        }
    }
}
```

**Edge cases**:
- `/proc/{pid}` existence check is an O(1) filesystem stat — much faster than
  `Process.GetProcessById(pid)` which throws an exception for missing PIDs.
- `/proc/{pid}/cmdline` uses null bytes (`\0`) as argument separators. Replace them
  with spaces before doing substring search.
- The `_restartHistory` queue is not persisted to disk — it resets on daemon restart.
  This is intentional: a daemon restart is a reset event for crash loop detection.
- `ExpireOrphanSessionsAsync` requires `IDebugSessionManager` which is implemented
  in P5.7. Use `null` or a no-op stub until P5.7 is complete; register the
  dependency as `Lazy<IDebugSessionManager>` to avoid circular DI ordering.

**Testing requirements**:
- Unit test: crash loop — 5 restarts in 60s → `IsInCrashLoop` returns true
- Unit test: 4 restarts in 60s → `IsInCrashLoop` returns false
- Unit test: restarts older than 60s are evicted from the window
- Unit test: `ReconcileProcessesAsync` clears PID for dead processes
- Integration test: kill managed app → ProcessMonitorService restarts it within 10s

**Definition of done**:
- [ ] `PeriodicTimer(5s)` polling loop
- [ ] Startup reconciliation reads `/proc/{pid}/cmdline`
- [ ] Crash loop detection: 5 restarts in 60s stops auto-restart
- [ ] Crash loop detected state logged at `Error` level
- [ ] Orphan session expiry calls `StopDebugSessionAsync`
- [ ] All tests pass

---

## P5.4 — Process gRPC RPCs

**Purpose**: Connect the process lifecycle RPCs in `MeadowDaemonGrpcService` to
`IProcessManager`, including the `StreamOutput` server-streaming RPC.

**Dependencies**: P2.3, P5.1, P5.2

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (implement RPCs)

**Implementation details**:

Add `IProcessManager` to the gRPC service constructor. Implement:

```csharp
public override async Task<StartProcessResponse> StartProcess(
    StartProcessRequest request, ServerCallContext context)
{
    LogCall(nameof(StartProcess), context);
    ValidateAppName(request.AppName);
    var result = await _processManager.StartAsync(request.AppName, context.CancellationToken);
    return new StartProcessResponse
    {
        Success = result.Success,
        Pid     = result.Pid ?? 0,
        Error   = result.Error ?? ""
    };
}

public override async Task<StopProcessResponse> StopProcess(
    StopProcessRequest request, ServerCallContext context)
{
    LogCall(nameof(StopProcess), context);
    ValidateAppName(request.AppName);
    await _processManager.StopAsync(request.AppName, context.CancellationToken);
    return new StopProcessResponse { Success = true };
}

public override async Task<GetProcessStatusResponse> GetProcessStatus(
    GetProcessStatusRequest request, ServerCallContext context)
{
    LogCall(nameof(GetProcessStatus), context);
    ValidateAppName(request.AppName);
    var state = _processManager.GetState(request.AppName);
    var pid   = _processManager.GetPid(request.AppName);
    return new GetProcessStatusResponse
    {
        Status = new ApplicationStatus
        {
            AppName = request.AppName,
            State   = state,
            Pid     = pid ?? 0,
        }
    };
}

public override async Task StreamOutput(
    StreamOutputRequest request,
    IServerStreamWriter<OutputLine> responseStream,
    ServerCallContext context)
{
    LogCall(nameof(StreamOutput), context);
    ValidateAppName(request.AppName);
    var broadcaster = _processManager.GetOutputBroadcaster(request.AppName);
    var ct = context.CancellationToken;

    await foreach (var line in broadcaster.Subscribe(ct))
    {
        if (request.Stream != OutputStream.Combined
            && line.Stream != request.Stream) continue;

        try { await responseStream.WriteAsync(line, ct); }
        catch (OperationCanceledException) { break; }
        catch { break; }
    }
}
```

**Edge cases**:
- `StreamOutput` for an app that is not running returns an empty stream (broadcaster
  has no lines buffered). This is correct — the subscriber waits for future lines.
- `request.Stream` can be `Stdout`, `Stderr`, or `Combined`. Filter accordingly.
- `GetProcessStatus` for an unknown app name returns `AppState.Unknown` (not throws).

**Testing requirements**:
- Integration test: start app, stream output, verify lines arrive
- Integration test: `StreamOutput` for stopped app → stream ends when app exits
- Integration test: `GetProcessStatus` for unknown app returns `AppState.Unknown`
- Integration test: `StopProcess` stops a running app

**Definition of done**:
- [ ] All 6 process RPCs implemented (Start, Stop, Restart, GetStatus, List, StreamOutput)
- [ ] `StreamOutput` filters by stdout/stderr/combined
- [ ] `ValidateAppName` called for all RPCs
- [ ] All integration tests pass

---

## P5.5 — VsdbgInstaller

**Purpose**: Install vsdbg on the Pi either by running `GetVsDbg.sh` (online) or by
extracting an uploaded tarball (offline), streaming progress back to the VSIX.

**Dependencies**: P1.5, P1.6

**Files**:
- `Source/Meadow.Daemon/Services/VsdbgInstaller.cs`
- `Source/Meadow.Daemon/Services/IVsdbgInstaller.cs`

**Implementation details**:

```csharp
public interface IVsdbgInstaller
{
    Task<bool> IsInstalledAsync(string requiredVersion);
    Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct);
    Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256, IProgress<string> progress, CancellationToken ct);
    string? GetInstalledVersion();
}

public sealed class VsdbgInstaller : IVsdbgInstaller
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VsdbgInstaller> _logger;

    public string? GetInstalledVersion()
    {
        var vf = DaemonPaths.VsdbgVersionFile(_options);
        if (!File.Exists(vf)) return null;
        return File.ReadAllText(vf).Trim();
    }

    public Task<bool> IsInstalledAsync(string requiredVersion)
    {
        var version = GetInstalledVersion();
        if (version is null) return Task.FromResult(false);
        if (!File.Exists(DaemonPaths.VsdbgBinPath(_options))) return Task.FromResult(false);
        // Version check: installed must be >= required (semver prefix match for "17.x")
        return Task.FromResult(VersionSatisfies(version, requiredVersion));
    }

    public async Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Downloading GetVsDbg.sh...");
        var scriptPath = Path.Combine(DaemonPaths.TempDir(), "GetVsDbg.sh");

        using var http = new HttpClient();
        var script = await http.GetStringAsync(
            "https://aka.ms/getvsdbgsh", ct);
        await File.WriteAllTextAsync(scriptPath, script, ct);

        // Make executable
        Mono.Unix.Native.Syscall.chmod(scriptPath, FilePermissions.S_IRWXU);

        var vsdbgDir = DaemonPaths.VsdbgDir(_options);
        var args     = $"-Version {version} -RuntimeID linux-arm64 -InstallPath {vsdbgDir}";

        progress.Report($"Running GetVsDbg.sh {args}...");
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("bash", $"{scriptPath} {args}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            }
        };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) progress.Report(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) progress.Report($"ERR: {e.Data}"); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new VsdbgInstallException($"GetVsDbg.sh failed with exit code {process.ExitCode}");

        progress.Report("vsdbg installed successfully.");
    }

    public async Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256,
        IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Receiving vsdbg tarball...");
        var tarPath = Path.Combine(DaemonPaths.TempDir(), "vsdbg.tar.gz");

        await using (var fs = File.Create(tarPath))
            await tarball.CopyToAsync(fs, ct);

        // Verify integrity
        progress.Report("Verifying tarball integrity...");
        var actualSha256 = await ComputeSha256Async(tarPath, ct);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new VsdbgInstallException(
                $"Tarball SHA-256 mismatch. Expected: {expectedSha256}, got: {actualSha256}");

        // Extract
        progress.Report($"Extracting to {DaemonPaths.VsdbgDir(_options)}...");
        Directory.CreateDirectory(DaemonPaths.VsdbgDir(_options));
        var extract = new Process
        {
            StartInfo = new ProcessStartInfo("tar",
                $"-xzf {tarPath} -C {DaemonPaths.VsdbgDir(_options)}")
            {
                UseShellExecute = false,
            }
        };
        extract.Start();
        await extract.WaitForExitAsync(ct);
        if (extract.ExitCode != 0)
            throw new VsdbgInstallException("tar extraction failed");

        // Set execute bit
        Mono.Unix.Native.Syscall.chmod(DaemonPaths.VsdbgBinPath(_options),
            FilePermissions.S_IRWXU | FilePermissions.S_IRGRP | FilePermissions.S_IXGRP);

        File.Delete(tarPath);
        progress.Report("vsdbg installed from offline tarball.");
    }
}
```

**Edge cases**:
- `https://aka.ms/getvsdbgsh` requires network access. If unavailable, the call throws
  `HttpRequestException`. The `InstallVsdbg` gRPC RPC should catch this and surface a
  clear error with fallback instructions (use offline tarball upload).
- Tarball SHA-256 verification must happen before extraction, not after. The expected
  SHA-256 is sent by the VSIX (it knows the hash of the bundled tarball).
- `VersionSatisfies("17.12.0", "17.x")` — implement a simple wildcard match:
  `"17.x"` matches any `17.*.*`. Alternatively, parse as semver and compare major.
- After online install, write the installed version to `.version` file.
  `GetVsDbg.sh` may do this automatically — check the installed directory.

**Testing requirements**:
- Unit test: `GetInstalledVersion()` returns null when `.version` file absent
- Unit test: `VersionSatisfies("17.12.0", "17.x")` → true
- Unit test: `VersionSatisfies("16.0.0", "17.x")` → false
- Integration test: `InstallFromTarballAsync` with a pre-downloaded tarball
- Integration test: tarball SHA-256 mismatch → throws `VsdbgInstallException`

**Definition of done**:
- [ ] `InstallAsync` downloads and runs `GetVsDbg.sh`
- [ ] `InstallFromTarballAsync` verifies SHA-256 before extraction
- [ ] Version file written after install
- [ ] Execute bit set on `vsdbg-ui` after install
- [ ] Online install failure surfaces a clear error
- [ ] All tests pass

---

## P5.6 — VsdbgLauncher

**Purpose**: Start vsdbg in server mode (`--server --port N`) and wait until it is
actually listening on the port before returning, so the VSIX can establish its debug
connection immediately.

**Dependencies**: P5.5, P1.5

**Files**:
- `Source/Meadow.Daemon/Services/VsdbgLauncher.cs`

**Implementation details**:

```csharp
public sealed class VsdbgProcess : IDisposable
{
    public int     Pid    { get; init; }
    public int     Port   { get; init; }
    public Process Handle { get; init; } = null!;
    public void Dispose() => Handle.Dispose();
}

public sealed class VsdbgLauncher
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VsdbgLauncher> _logger;

    public async Task<VsdbgProcess> LaunchAsync(
        int port, int? attachPid, CancellationToken ct)
    {
        var vsdbgPath = DaemonPaths.VsdbgBinPath(_options);
        if (!File.Exists(vsdbgPath))
            throw new InvalidOperationException("vsdbg not installed");

        var args = $"--server --port {port}";
        if (attachPid.HasValue)
            args += $" --attach {attachPid.Value}";

        _logger.LogInformation("Launching vsdbg: {Args}", args);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(vsdbgPath, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        // Capture vsdbg stderr for diagnostics
        var stderrBuf = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderrBuf.AppendLine(e.Data);
        };

        process.Start();
        process.BeginErrorReadLine();

        // Wait for vsdbg to be LISTEN on the port
        await WaitForPortAsync(port, timeout: TimeSpan.FromSeconds(10), ct);

        return new VsdbgProcess { Pid = process.Id, Port = port, Handle = process };
    }

    /// Polls /proc/net/tcp6 every 250ms until the port is in LISTEN state.
    private async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var hexPort = port.ToString("X4");
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (IsPortListening(hexPort))
            {
                _logger.LogInformation("vsdbg listening on port {Port}", port);
                return;
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException(
            $"vsdbg did not start listening on port {port} within {timeout.TotalSeconds}s");
    }

    private static bool IsPortListening(string hexPort)
    {
        // /proc/net/tcp6 format:
        // sl  local_address:port  rem_address:port  st  ...
        // 0A = TCP_LISTEN
        try
        {
            foreach (var line in File.ReadLines("/proc/net/tcp6").Skip(1))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                // local address is "00000000000000000000000001000000:PORT"
                var localPort = parts[1].Split(':').Last().ToUpperInvariant();
                var state     = parts[3];
                if (localPort == hexPort && state == "0A")  // 0A = LISTEN
                    return true;
            }
        }
        catch { /* /proc unavailable (non-Linux dev) */ }
        return false;
    }

    public int AllocatePort()
    {
        // Find a free port in the configured range
        for (var p = _options.VsdbgPortRangeStart; p <= _options.VsdbgPortRangeEnd; p++)
        {
            if (!IsPortListening(p.ToString("X4")))
                return p;
        }
        throw new InvalidOperationException(
            $"No free vsdbg port in range {_options.VsdbgPortRangeStart}-{_options.VsdbgPortRangeEnd}");
    }
}
```

**Edge cases**:
- `/proc/net/tcp6` only exists on Linux. On developer Windows machines, `IsPortListening`
  returns `false` immediately (caught by the outer `catch`). The wait loop will timeout.
  For Windows integration testing, mock `IsPortListening` or use a real Pi.
- Port hex format in `/proc/net/tcp6` uses uppercase hex with no leading `0x`.
  `port.ToString("X4")` produces `"09B8"` for port 2488. Verify this matches.
- vsdbg may also listen on `tcp` (IPv4) not `tcp6`. Check both
  `/proc/net/tcp` and `/proc/net/tcp6` for completeness.
- `AllocatePort` has a TOCTOU race: the port could be taken between check and use.
  Accept this — vsdbg port conflicts are unlikely and self-healing (next launch picks
  a different port).

**Testing requirements**:
- Unit test: `IsPortListening` parses a sample `/proc/net/tcp6` line correctly
- Unit test: `AllocatePort` skips ports already in LISTEN state
- Integration test (Linux): launch vsdbg, verify it starts within 5s
- Integration test (Linux): `WaitForPortAsync` returns when vsdbg is listening
- Integration test (Linux): timeout correctly thrown when port never binds

**Definition of done**:
- [ ] Launches `vsdbg-ui --server --port N [--attach PID]`
- [ ] Polls `/proc/net/tcp6` every 250ms for LISTEN state
- [ ] Checks both `/proc/net/tcp` and `/proc/net/tcp6`
- [ ] 10-second timeout for vsdbg to bind
- [ ] vsdbg stderr captured for diagnostics
- [ ] `AllocatePort` finds next free port in configured range
- [ ] All tests pass

---

## P5.7 — DebugSessionManager

**Purpose**: Manage the full lifecycle of a debug session: allocate a vsdbg port, launch
vsdbg, create and persist a session record, and clean up on stop or timeout.

**Dependencies**: P5.1, P5.6, P1.8, P1.5

**Files**:
- `Source/Meadow.Daemon/Services/DebugSessionManager.cs`
- `Source/Meadow.Daemon/Services/IDebugSessionManager.cs`

**Implementation details**:

```csharp
public interface IDebugSessionManager
{
    Task<DebugSessionRecord> StartDebugSessionAsync(
        string appName, SessionMode mode, string correlationId, CancellationToken ct);
    Task StopDebugSessionAsync(string sessionId, CancellationToken ct);
    Task<DebugSessionRecord?> GetSessionStatusAsync(string sessionId, CancellationToken ct);
    Task<IReadOnlyList<DebugSessionRecord>> ListSessionsAsync(CancellationToken ct);
    Task TouchSessionAsync(string sessionId, CancellationToken ct);  // heartbeat
}
```

```csharp
public sealed class DebugSessionManager : IDebugSessionManager
{
    private readonly IProcessManager _processManager;
    private readonly VsdbgLauncher _vsdbgLauncher;
    private readonly StateStore _stateStore;
    private readonly ILogger<DebugSessionManager> _logger;
    // Active vsdbg processes keyed by sessionId
    private readonly ConcurrentDictionary<string, VsdbgProcess> _vsdbgProcesses = new();

    public async Task<DebugSessionRecord> StartDebugSessionAsync(
        string appName, SessionMode mode, string correlationId, CancellationToken ct)
    {
        // Ensure app is running (start if not)
        if (_processManager.GetState(appName) != AppState.Running)
            await _processManager.StartAsync(appName, ct);

        var appPid = _processManager.GetPid(appName)
            ?? throw new InvalidOperationException($"App '{appName}' failed to start");

        // Allocate port and launch vsdbg
        var port     = _vsdbgLauncher.AllocatePort();
        var vsdbg    = await _vsdbgLauncher.LaunchAsync(
            port, mode == SessionMode.Attach ? appPid : null, ct);

        // Create session record
        var sessionId = NewUlid();
        var record = new DebugSessionRecord
        {
            SessionId     = sessionId,
            AppName       = appName,
            VsdbgPid      = vsdbg.Pid,
            VsdbgPort     = port,
            AppPid        = appPid,
            Mode          = mode,
            State         = SessionState.Ready,
            CorrelationId = correlationId,
        };

        _vsdbgProcesses[sessionId] = vsdbg;

        // Persist
        var state = await _stateStore.LoadSessionsAsync(ct);
        state.Sessions.Add(record);
        await _stateStore.SaveSessionsAsync(state, ct);

        _logger.LogInformation(
            "Debug session {Id} started for {App} on port {Port}",
            sessionId, appName, port);
        return record;
    }

    public async Task StopDebugSessionAsync(string sessionId, CancellationToken ct)
    {
        if (_vsdbgProcesses.TryRemove(sessionId, out var vsdbg))
        {
            try
            {
                vsdbg.Handle.Kill(entireProcessTree: true);
                await vsdbg.Handle.WaitForExitAsync(ct);
            }
            catch { /* vsdbg already exited */ }
            vsdbg.Dispose();
        }

        // Update state
        var state = await _stateStore.LoadSessionsAsync(ct);
        var session = state.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is not null)
        {
            session.State = SessionState.Stopped;
            await _stateStore.SaveSessionsAsync(state, ct);
        }

        _logger.LogInformation("Debug session {Id} stopped", sessionId);
    }

    public async Task TouchSessionAsync(string sessionId, CancellationToken ct)
    {
        var state   = await _stateStore.LoadSessionsAsync(ct);
        var session = state.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return;
        session.LastActivityAt = DateTimeOffset.UtcNow;
        await _stateStore.SaveSessionsAsync(state, ct);
    }
}
```

**Edge cases**:
- If vsdbg fails to start (e.g. PID is invalid, vsdbg binary is corrupt), the exception
  propagates to the gRPC caller. No session record is persisted.
- If the app exits while vsdbg is attached, vsdbg also exits. The `ProcessMonitorService`
  detects vsdbg exiting and updates the session state to `Failed`.
- `TouchSessionAsync` is called by `GetSessionStatus` RPC every ~30s from the VSIX.
  This resets the orphan timeout. If the VSIX disconnects, `LastActivityAt` stops
  updating and `ProcessMonitorService` eventually expires the session.

**Testing requirements**:
- Integration test: `StartDebugSessionAsync` starts vsdbg, returns valid session record
- Integration test: `StopDebugSessionAsync` kills vsdbg process
- Integration test: `TouchSessionAsync` updates `LastActivityAt`
- Integration test: session state persisted to `sessions.json`
- Unit test: missing app → `InvalidOperationException`

**Definition of done**:
- [ ] Starts vsdbg in `--attach PID` mode (Attach) or plain `--server --port N` (Launch)
- [ ] Session record persisted to `sessions.json`
- [ ] `StopDebugSessionAsync` kills vsdbg process
- [ ] `TouchSessionAsync` resets orphan timeout
- [ ] All integration tests pass

---

## P5.8 — Debug Session gRPC RPCs

**Purpose**: Implement all debug session and vsdbg management RPCs in
`MeadowDaemonGrpcService`, connecting them to `IDebugSessionManager` and
`IVsdbgInstaller`.

**Dependencies**: P2.3, P5.7, P5.5

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (implement RPCs)

**Implementation details**:

Add `IDebugSessionManager` and `IVsdbgInstaller` to constructor. Implement:

```csharp
public override async Task<StartDebugSessionResponse> StartDebugSession(
    StartDebugSessionRequest request, ServerCallContext context)
{
    LogCall(nameof(StartDebugSession), context);
    ValidateAppName(request.AppName);
    var session = await _sessionManager.StartDebugSessionAsync(
        request.AppName, request.Mode, request.CorrelationId, context.CancellationToken);
    return new StartDebugSessionResponse
    {
        SessionId = session.SessionId,
        VsdbgPort = session.VsdbgPort,
        AppPid    = session.AppPid ?? 0,
    };
}

public override async Task<GetSessionStatusResponse> GetSessionStatus(
    GetSessionStatusRequest request, ServerCallContext context)
{
    LogCall(nameof(GetSessionStatus), context);
    // Touch the session (heartbeat)
    await _sessionManager.TouchSessionAsync(request.SessionId, context.CancellationToken);
    var record = await _sessionManager.GetSessionStatusAsync(
        request.SessionId, context.CancellationToken);
    if (record is null)
        throw new RpcException(new Status(StatusCode.NotFound,
            $"Session '{request.SessionId}' not found"));
    return new GetSessionStatusResponse { Status = MapSessionStatus(record) };
}

public override async Task InstallVsdbg(
    InstallVsdbgRequest request,
    IServerStreamWriter<InstallVsdbgProgress> responseStream,
    ServerCallContext context)
{
    LogCall(nameof(InstallVsdbg), context);
    var progress = new Progress<string>(async msg =>
    {
        try { await responseStream.WriteAsync(
                new InstallVsdbgProgress { Message = msg },
                context.CancellationToken); }
        catch { /* subscriber gone */ }
    });
    await _vsdbgInstaller.InstallAsync(request.Version, progress, context.CancellationToken);
    await responseStream.WriteAsync(new InstallVsdbgProgress
    {
        Message = "complete",
        Done    = true,
    });
}

public override async Task<UploadVsdbgTarballResponse> UploadVsdbgTarball(
    IAsyncStreamReader<UploadVsdbgTarballRequest> requestStream,
    ServerCallContext context)
{
    LogCall(nameof(UploadVsdbgTarball), context);
    // Accumulate chunks into a MemoryStream, then pass to installer
    using var buffer = new MemoryStream();
    string expectedSha256 = "";
    await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
    {
        if (chunk.HasSha256) expectedSha256 = chunk.Sha256;
        buffer.Write(chunk.Data.Span);
    }
    buffer.Position = 0;
    await _vsdbgInstaller.InstallFromTarballAsync(buffer, expectedSha256,
        new Progress<string>(_ => { }), context.CancellationToken);
    return new UploadVsdbgTarballResponse { Success = true };
}
```

**Edge cases**:
- `InstallVsdbg` is a server-streaming RPC. The `Progress<string>` callback invokes
  `responseStream.WriteAsync` from a background thread. This is safe with gRPC-dotnet
  (it uses `Channel<T>` internally), but the callback must handle the case where the
  RPC is cancelled (client disconnected) by catching `OperationCanceledException`.
- `UploadVsdbgTarball` accumulates all chunks in a `MemoryStream`. The vsdbg tarball
  is ~55 MB — this is within the 64 MB `MaxReceiveMessageSize`. For larger files,
  use a `FileStream` temporary file instead.
- `GetSessionStatus` must call `TouchSessionAsync` on every call to reset the orphan
  timeout. The VSIX is expected to call this every ~30s.

**Testing requirements**:
- Integration test: `StartDebugSession` returns `VsdbgPort` > 0
- Integration test: `GetSessionStatus` updates `LastActivityAt`
- Integration test: `InstallVsdbg` streams progress messages
- Integration test: `UploadVsdbgTarball` with a valid tarball installs vsdbg
- Integration test: `StopDebugSession` kills vsdbg

**Definition of done**:
- [ ] All 7 session/vsdbg RPCs implemented
- [ ] `GetSessionStatus` calls `TouchSessionAsync` on every call
- [ ] `InstallVsdbg` progress streamed back to caller
- [ ] `UploadVsdbgTarball` chunks assembled and passed to `InstallFromTarballAsync`
- [ ] `NotFound` returned for unknown session IDs
- [ ] All integration tests pass

---

## P5.9 — VSIX Debug Launch Provider

**Purpose**: Intercept the F5 key press for PiDbg projects and orchestrate the full
deploy-then-debug workflow: provision → publish → deploy → start debug session →
configure VS debug engine → establish SSH debug tunnel → launch VS debugger.

**Dependencies**: P4.6, P4.7, P4.8, P5.10, Phase 6 (ProvisioningOrchestrator)

**Files**:
- `Source/VsExtension/Debug/PiDbgDebugLaunchProvider.cs`

**Implementation details**:

```csharp
[Export(typeof(IDebugLaunchProvider))]
[AppliesTo(PiDbgCapability)]
public sealed class PiDbgDebugLaunchProvider : IDebugLaunchProvider2
{
    public const string PiDbgCapability = "PiDbg";

    [Import] private SVsServiceProvider _serviceProvider = null!;

    public async Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        // Only handle projects with PiDbgHost set
        var props = await GetProjectPropertiesAsync();
        return !string.IsNullOrEmpty(await props.GetHostAsync());
    }

    public async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
        DebugLaunchOptions launchOptions)
    {
        var output    = GetService<IOutputWindowService>();
        var props     = await GetProjectPropertiesAsync();
        var config    = await props.GetConnectionConfigAsync()
                        ?? throw new InvalidOperationException("PiDbgHost not set");
        var appName   = await props.GetAppNameAsync();
        var project   = await GetActiveProjectPathAsync();

        output.Activate(OutputPane.PiDbg);
        output.WriteLine(OutputPane.PiDbg, $"=== PiDbg: Starting {appName} ===");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // 1. SSH connection
        var ssh = GetService<ISshConnectionManager>();
        var session = await ssh.ConnectAsync(config, ct);

        // 2. Provision (idempotent)
        var provisioner = new ProvisioningOrchestrator(session, output, this);
        await provisioner.ProvisionAsync(ct);

        // 3. Publish
        var publisher = new PublishService(output);
        var publishResult = await publisher.PublishAsync(project, appName,
            new Progress<string>(s => output.WriteLine(OutputPane.PiDbg, s)), ct);

        // 4. Deploy
        var grpcChannel = await GetService<IGrpcChannelFactory>()
            .GetOrCreateChannelAsync(session, ct);
        var deployer = new SftpDeploymentClient(session, grpcChannel, output);
        await deployer.DeployAsync(appName, publishResult.PublishDir,
            publishResult.Manifest,
            new Progress<DeploymentProgress>(p =>
                output.WriteLine(OutputPane.PiDbg,
                    $"  [{p.Phase}] {p.PercentComplete}%")),
            ct);

        // 5. Start debug session
        var client    = new MeadowDaemonService.MeadowDaemonServiceClient(grpcChannel);
        var sessionResp = await client.StartDebugSessionAsync(new StartDebugSessionRequest
        {
            AppName       = appName,
            Mode          = SessionMode.Attach,
            CorrelationId = Guid.NewGuid().ToString(),
        }, cancellationToken: ct);

        // 6. Open SSH tunnel for vsdbg port
        var tunnelMgr  = GetService<IDebugTunnelManager>();
        var localPort  = await tunnelMgr.OpenDebugTunnelAsync(
            session, sessionResp.VsdbgPort, ct);

        output.WriteLine(OutputPane.PiDbg,
            $"Debug tunnel: localhost:{localPort} → {config.Host}:{sessionResp.VsdbgPort}");

        // 7. Build VsDebugTargetInfo4
        var settings = new VsDebugTargetInfo4
        {
            dlo                 = (uint)DebugLaunchOperation.AlreadyRunning,
            LaunchFlags         = (uint)launchOptions,
            guidLaunchDebugEngine = new Guid("{2E36F1D4-B23C-435D-AB41-18E608940038}"),
            bstrRemoteMachine   = $"127.0.0.1:{localPort}",
            bstrOptions         = BuildDebugOptions(publishResult, sessionResp, localPort),
            bstrExe             = $"{appName}.dll",
        };

        return [new VsDebugTargetInfoWrapper(settings)];
    }

    private static string BuildDebugOptions(
        PublishResult publish, StartDebugSessionResponse session, int localPort)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            transport  = "tcp",
            port       = localPort,
            host       = "127.0.0.1",
            sessionId  = session.SessionId,
        });
    }
}
```

**Edge cases**:
- `guidLaunchDebugEngine = {2E36F1D4-B23C-435D-AB41-18E608940038}` is the .NET managed
  debug engine GUID. This is hardcoded by the VS SDK and must not change.
- `dlo = AlreadyRunning` (value 4) tells VS that the process is already running and
  it should attach, not launch. This is correct for Attach mode.
- `bstrRemoteMachine` uses the local tunnel port, not the Pi's port. VS connects to
  `127.0.0.1:{localPort}` which is forwarded by SSH to `{pi}:{vsdbgPort}`.
- The 5-minute `CancellationTokenSource` covers the entire F5 → debug-attached pipeline.
  If any step takes > 5 minutes, the whole operation is cancelled.
- `QueryDebugTargetsAsync` runs on the VS UI thread. All async work must be properly
  awaited. Do not block with `.Result` or `.Wait()`.

**Testing requirements**:
- Integration test: full F5 cycle on a real Pi — app launches, VS attaches, breakpoint hits
- Integration test: cancel F5 mid-deploy — deployment aborted cleanly
- Manual test: error during provisioning → clear error in Output window, no VS crash

**Definition of done**:
- [ ] `CanLaunchAsync` returns false when `PiDbgHost` not set
- [ ] `QueryDebugTargetsAsync` runs full provision → publish → deploy → session pipeline
- [ ] `guidLaunchDebugEngine = {2E36F1D4-B23C-435D-AB41-18E608940038}`
- [ ] `dlo = AlreadyRunning` (value 4)
- [ ] `bstrRemoteMachine = 127.0.0.1:{localTunnelPort}`
- [ ] 5-minute timeout on entire pipeline
- [ ] Integration test on real Pi passes

---

## P5.10 — SSH Debug Tunnel

**Purpose**: Open an SSH port-forward tunnel for the vsdbg port so the VS debug engine
connects to `localhost` while traffic flows through SSH to the Pi.

**Dependencies**: P4.3

**Files**:
- `Source/VsExtension/Infrastructure/DebugTunnelManager.cs`
- `Source/VsExtension/Infrastructure/IDebugTunnelManager.cs`

**Implementation details**:

```csharp
public interface IDebugTunnelManager
{
    Task<int> OpenDebugTunnelAsync(SshSession session, int vsdbgPort, CancellationToken ct);
    void CloseDebugTunnel(int localPort);
    void CloseAllTunnels();
}

public sealed class DebugTunnelManager : IDebugTunnelManager, IDisposable
{
    private readonly ConcurrentDictionary<int, ForwardedPortLocal> _tunnels = new();
    private readonly ILogger<DebugTunnelManager> _logger;

    public async Task<int> OpenDebugTunnelAsync(
        SshSession session, int vsdbgPort, CancellationToken ct)
    {
        var (fwd, localPort) = await session.OpenTunnelAsync(vsdbgPort, ct);
        _tunnels[localPort] = fwd;

        // Keep-alive: vsdbg sessions can be long-lived
        session.Ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _logger.LogInformation(
            "Debug tunnel open: 127.0.0.1:{Local} → {Host}:{Remote}",
            localPort, session.Host, vsdbgPort);

        return localPort;
    }

    public void CloseDebugTunnel(int localPort)
    {
        if (_tunnels.TryRemove(localPort, out var fwd))
        {
            fwd.Stop();
            _logger.LogInformation("Debug tunnel closed: 127.0.0.1:{Port}", localPort);
        }
    }

    public void CloseAllTunnels()
    {
        foreach (var (port, fwd) in _tunnels)
        {
            fwd.Stop();
            _logger.LogInformation("Debug tunnel closed: 127.0.0.1:{Port}", port);
        }
        _tunnels.Clear();
    }

    public void Dispose() => CloseAllTunnels();
}
```

Tunnel lifetime management:
- Tunnel is opened just before `VsDebugTargetInfo4` is constructed
- Tunnel must remain open for the entire debug session
- Tunnel is closed when:
  - The debug session ends (VS fires `IDebugSessionDestroyedEvent2`)
  - The VS instance closes
  - `CloseAllTunnels` is called from `PiDbgPackage.Dispose`

**Edge cases**:
- `ForwardedPortLocal` with `localPort=0` asks the OS to assign a free port.
  `fwd.BoundPort` returns the assigned port after `fwd.Start()`.
- Keep-alive packets keep the SSH connection alive during long debug sessions. Without
  them, the connection may timeout after 2 hours of idle debugging.
- If the SSH connection drops while debugging, the tunnel stops. The VS debug engine
  receives a disconnect and shows "The remote debugging connection lost". This is
  the correct failure mode — no data corruption, clean error.
- Multiple concurrent debug sessions (multiple projects/apps) each get their own tunnel
  on different local ports.

**Testing requirements**:
- Unit test: tunnel opens on port 0 and returns a non-zero local port
- Integration test: data sent to local port arrives at remote port
- Integration test: `CloseDebugTunnel` stops the tunnel
- Integration test: keep-alive packets verified in SSH server logs

**Definition of done**:
- [ ] `OpenDebugTunnelAsync` uses `ForwardedPortLocal` with port 0
- [ ] Returns OS-assigned local port (non-zero)
- [ ] Keep-alive interval set to 30s
- [ ] Tunnel tracked by local port, closed individually or all at once
- [ ] `Dispose` closes all open tunnels
- [ ] Registered in `PiDbgPackage` for cleanup on VS close
