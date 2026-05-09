# Meadow.Daemon — Process Manager

---

## 1. Responsibilities

`ProcessManager` supervises the managed application's process lifecycle:
- Start / stop / restart the app
- Track the running PID across daemon restarts
- Stream stdout/stderr to gRPC subscribers
- Publish process exit events for `ProcessMonitorService`

One `SemaphoreSlim(1)` per app prevents concurrent start/stop operations on the same app.

---

## 2. Application State Machine

```
        ┌──────────┐
        │  Stopped  │ ◄──────────────────────────────────┐
        └────┬─────┘                                      │
             │ StartApplicationAsync                       │
             ▼                                            │
        ┌──────────┐                                      │
        │ Starting  │                                      │
        └────┬─────┘                                      │
             │ Process spawned                            │
             ▼                                            │
        ┌──────────┐   StopApplicationAsync   ┌──────────┐
        │ Running  │ ─────────────────────────► Stopping  │
        └────┬─────┘                           └────┬─────┘
             │ Process exits (unexpected)            │ SIGTERM + timeout
             ▼                                       ▼
        ┌──────────┐                          ┌──────────┐
        │  Failed   │                         │  Stopped  │
        └──────────┘                          └──────────┘
```

`AppState` enum matches the proto definition in `process.proto`:
```csharp
public enum AppState
{
    Unknown  = 0,
    Starting = 1,
    Running  = 2,
    Stopping = 3,
    Stopped  = 4,
    Failed   = 5,
}
```

---

## 3. ProcessManager Class

```csharp
internal sealed class ProcessManager
{
    // Start the app from its active deployment (or debug slot if debugVersion set)
    public Task<StartResult> StartApplicationAsync(
        string appName, bool useDebugSlot, CancellationToken ct);

    // Graceful stop: SIGTERM → gracePeriod → SIGKILL
    public Task<StopResult> StopApplicationAsync(
        string appName, TimeSpan? gracePeriod, CancellationToken ct);

    // Stop + Start
    public Task<StartResult> RestartApplicationAsync(
        string appName, bool useDebugSlot, CancellationToken ct);

    // Current state snapshot
    public Task<ApplicationStatus> GetStatusAsync(string appName, CancellationToken ct);

    // Subscribe to stdout/stderr (async stream, cancellable)
    public IAsyncEnumerable<OutputLine> StreamOutputAsync(
        string appName, CancellationToken ct);

    // All tracked app names
    public IReadOnlyCollection<string> TrackedApps { get; }

    // Raised by ProcessMonitorService when a tracked process exits
    public event EventHandler<ProcessExitedEventArgs> ProcessExited;
}
```

---

## 4. Process Startup

```csharp
private async Task<Process> SpawnAsync(
    AppRecord record, string appDir, CancellationToken ct)
{
    var manifest = await _deploymentManager.GetManifestAsync(appDir);
    var entryPoint = Path.Combine(appDir, manifest.EntryPoint);

    var psi = new ProcessStartInfo
    {
        FileName = "dotnet",
        ArgumentList = { entryPoint },
        WorkingDirectory = appDir,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = false,
    };

    // Startup args from manifest override stored record
    if (!string.IsNullOrWhiteSpace(manifest.StartupArgs))
        foreach (var arg in SplitArgs(manifest.StartupArgs))
            psi.ArgumentList.Add(arg);

    // Environment: system environment base + manifest overrides + record overrides
    foreach (var (k, v) in manifest.EnvironmentVariables)
        psi.Environment[k] = v;
    foreach (var (k, v) in record.EnvironmentVariables)
        psi.Environment[k] = v;

    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
    process.Start();

    _log.LogInformation("Started {App} (PID {Pid}) from {Dir}",
        record.Name, process.Id, appDir);

    return process;
}
```

After spawn, `ProcessManager`:
1. Records the new PID in `ManagedAppState`
2. Starts `ProcessOutputBroadcaster` for the process
3. Registers an `Exited` handler that fires `ProcessExited` event
4. Calls `StateStore.UpdatePidAsync(appName, pid)` (async, fire-and-forget)

---

## 5. Process Stop

```csharp
private async Task StopProcessAsync(
    int pid, TimeSpan gracePeriod, CancellationToken ct)
{
    if (!IsAlive(pid))
    {
        _log.LogDebug("Process {Pid} already dead, skip stop", pid);
        return;
    }

    // Send SIGTERM
    try { Process.GetProcessById(pid).Kill(entireProcessTree: false); }
    catch (ArgumentException) { return; } // already gone

    // Actually send SIGTERM not SIGKILL — use Mono.Unix for POSIX signals
    Syscall.kill(pid, Signum.SIGTERM);

    // Wait for graceful exit
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(gracePeriod);

    try
    {
        await WaitForExitAsync(pid, cts.Token);
        _log.LogInformation("Process {Pid} exited gracefully", pid);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
        // Grace period expired → SIGKILL
        _log.LogWarning("Process {Pid} did not exit in {Grace}s, sending SIGKILL",
            pid, gracePeriod.TotalSeconds);
        Syscall.kill(pid, Signum.SIGKILL);
    }
}
```

`WaitForExitAsync` polls `/proc/{pid}` existence every 50ms. This avoids `Process.WaitForExit`
which requires the process handle to remain valid.

`gracePeriod` default is `ProcessGracefulStopSeconds` from config (default 5 seconds).

---

## 6. ProcessOutputBroadcaster

Captures stdout/stderr and fans out to multiple gRPC stream subscribers.

```csharp
internal sealed class ProcessOutputBroadcaster : IAsyncDisposable
{
    // Channel bounded at 2000 lines; oldest dropped when full
    private readonly Channel<OutputLine> _channel;
    private readonly List<ChannelWriter<OutputLine>> _subscribers = new();
    private readonly SemaphoreSlim _subLock = new(1);

    public ProcessOutputBroadcaster(Process process)
    {
        _channel = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(2000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false,
        });

        // Pump stdout
        Task.Run(() => PumpStreamAsync(process.StandardOutput, OutputStream.Stdout));
        // Pump stderr
        Task.Run(() => PumpStreamAsync(process.StandardError, OutputStream.Stderr));
    }

    // Returns an AsyncEnumerable that yields lines until ct is cancelled or process exits
    public async IAsyncEnumerable<OutputLine> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sub = Channel.CreateUnbounded<OutputLine>();
        await _subLock.WaitAsync(ct);
        _subscribers.Add(sub.Writer);
        _subLock.Release();

        try
        {
            await foreach (var line in sub.Reader.ReadAllAsync(ct))
                yield return line;
        }
        finally
        {
            await _subLock.WaitAsync(CancellationToken.None);
            _subscribers.Remove(sub.Writer);
            sub.Writer.TryComplete();
            _subLock.Release();
        }
    }

    private async Task PumpStreamAsync(StreamReader reader, OutputStream stream)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            var entry = new OutputLine
            {
                Stream = stream,
                Text = line,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };

            // Fan out to all subscribers
            await _subLock.WaitAsync();
            foreach (var sub in _subscribers)
                sub.TryWrite(entry); // non-blocking, subscriber catches up or drops
            _subLock.Release();
        }

        // Process stdout/stderr closed → signal all subscribers
        await _subLock.WaitAsync();
        foreach (var sub in _subscribers)
            sub.TryComplete();
        _subLock.Release();
    }
}
```

---

## 7. StreamOutput gRPC Handler

```csharp
// In MeadowDaemonGrpcService:
public override async Task StreamOutput(
    StreamOutputRequest request,
    IServerStreamWriter<OutputLine> responseStream,
    ServerCallContext context)
{
    await foreach (var line in _processManager.StreamOutputAsync(
        request.AppName, context.CancellationToken))
    {
        await responseStream.WriteAsync(line, context.CancellationToken);
    }
}
```

The gRPC stream completes when the process exits (all subscribers' channels are completed).
If the client disconnects, `context.CancellationToken` cancels the `IAsyncEnumerable`,
which removes the subscriber from `ProcessOutputBroadcaster._subscribers`.

---

## 8. ManagedAppState

In-memory state per tracked app, protected by `_appLocks[appName]`:

```csharp
internal sealed class ManagedAppState
{
    public string AppName { get; init; } = "";
    public AppState State { get; set; } = AppState.Stopped;
    public int? Pid { get; set; }
    public Process? Process { get; set; }
    public ProcessOutputBroadcaster? OutputBroadcaster { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public int? LastExitCode { get; set; }
    public string? LastExitReason { get; set; }  // "exited", "killed", "crashed"
}
```

`ConcurrentDictionary<string, ManagedAppState>` holds one entry per tracked app.

---

## 9. ProcessMonitorService (IHostedService)

Background service that watches all tracked PIDs and triggers auto-restart policy.

```csharp
internal sealed class ProcessMonitorService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Phase 1: startup reconciliation
        await ReconcileStateAsync(stoppingToken);

        // Phase 2: watch loop — poll every 5 seconds
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await CheckTrackedProcessesAsync(stoppingToken);
        }
    }

    private async Task ReconcileStateAsync(CancellationToken ct)
    {
        // Load apps.json → for each app with a saved PID:
        //   1. Check /proc/{pid}/cmdline matches expected app
        //   2. If alive + matches → re-adopt process
        //   3. If dead + autoStart=true → schedule restart (3s delay)
        // Also heal symlink/state discrepancies (see deployment doc §11)
    }

    private async Task CheckTrackedProcessesAsync(CancellationToken ct)
    {
        foreach (var state in _processManager.GetAllStates())
        {
            if (state.State != AppState.Running) continue;
            if (state.Pid is { } pid && !IsProcessAlive(pid))
            {
                _log.LogWarning("Managed process {App} (PID {Pid}) died unexpectedly",
                    state.AppName, pid);
                _processManager.NotifyProcessDied(state.AppName, pid);

                var record = await _stateStore.GetAppRecordAsync(state.AppName, ct);
                if (record?.AutoStart == true)
                {
                    _log.LogInformation("Auto-restarting {App} in 3s", state.AppName);
                    _ = Task.Delay(3_000, ct)
                        .ContinueWith(_ => _processManager
                            .StartApplicationAsync(state.AppName, useDebugSlot: false, ct),
                            ct, TaskContinuationOptions.NotOnCanceled,
                            TaskScheduler.Default);
                }
            }
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { return File.Exists($"/proc/{pid}"); }
        catch { return false; }
    }
}
```

---

## 10. Two-Pass Stale Process Detection

Before every debug session, the orchestrator (VSIX-side) calls `StopApplicationAsync`,
but the daemon also implements internal stale detection as a defense layer.

### Pass 1: Tracked PID

Check `ManagedAppState.Pid` and verify the process is for the expected app by reading
`/proc/{pid}/cmdline`:

```csharp
private async Task<bool> IsPidOurApp(int pid, string appName)
{
    try
    {
        var cmdline = await File.ReadAllTextAsync($"/proc/{pid}/cmdline");
        // cmdline is NUL-separated: "dotnet\0/opt/meadow/apps/MyApp/..."
        return cmdline.Contains(appName, StringComparison.OrdinalIgnoreCase);
    }
    catch { return false; }
}
```

### Pass 2: Full /proc Scan

If tracked PID is stale or unknown, scan all PIDs:

```csharp
private async Task<int?> FindAppProcessAsync(string appName, CancellationToken ct)
{
    foreach (var dir in Directory.EnumerateDirectories("/proc"))
    {
        if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
        if (await IsPidOurApp(pid, appName)) return pid;
    }
    return null;
}
```

This scan runs in under 100ms on a Pi 4 with a typical process count. It is only
triggered if Pass 1 fails.

---

## 11. ProcessExitedEventArgs

```csharp
public sealed class ProcessExitedEventArgs : EventArgs
{
    public string AppName { get; init; } = "";
    public int Pid { get; init; }
    public int? ExitCode { get; init; }
    public string Reason { get; init; } = "";  // "exited", "killed", "signal"
}
```

The `DebugSessionManager` subscribes to `ProcessManager.ProcessExited` to clean up
any active debug session when the app exits unexpectedly during debugging.
