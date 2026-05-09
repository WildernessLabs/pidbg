# PiDbg — Threading Model

---

## 1. VSIX Thread Rules

Visual Studio has strict UI thread requirements. Violating them produces hard-to-diagnose
errors or hangs.

### Rule 1: VS APIs on UI thread only
Any call to a VS service (IVsOutputWindow, IVsDebugger4, IBuildManager, any IVs* interface)
MUST run on the VS UI thread.

Pattern for switching to UI thread:
```csharp
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
vsService.DoUiThing();
```

Pattern for switching off UI thread (before any I/O):
```csharp
await TaskScheduler.Default; // switches to thread pool
// now safe to do SSH, gRPC, file I/O
```

### Rule 2: Never block the UI thread
No `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()` anywhere in VSIX code.
The sole exception is VS SDK synchronous entry points (package initialization methods
that are synchronous by design) — these must use `JoinableTaskFactory.Run()`.

### Rule 3: ConfigureAwait(false) everywhere in library code
`PiDbg.Transport`, `PiDbg.Deployment`, `PiDbg.DeviceManagement`, `PiDbg.Contracts`
all use `ConfigureAwait(false)` on every await. These libraries have no knowledge of
VS's `SynchronizationContext` and must not capture it.

VSIX code (which runs with VS's JoinableTaskContext) uses `ConfigureAwait(false)` by
default and only switches to UI thread explicitly when needed.

---

## 2. VSIX Thread Map

```
VS UI Thread (STA)
│
├── VS Extension Load                    [async, JoinableTaskFactory]
├── Menu command handlers                [switch to background for work]
├── Debug launch providers               [QueryDebugTargetsAsync — background ok]
├── Tool window init                     [UI thread required for WPF]
├── Property page changes                [UI thread for VS notification]
└── Output window writes                 [must marshal here]

Background Thread Pool
│
├── SSH connection                        [SshConnectionManager]
├── SFTP file transfer                   [SftpTransferService]
├── gRPC calls                           [AgentClientWrapper]
├── Build trigger                        [IBuildManager async path]
├── vsdbg port availability polling      [Task.Delay polling loop]
└── Log streaming from agent             [IAsyncEnumerable consumption]
```

---

## 3. Agent Thread Model

The agent runs on .NET 10 with `Microsoft.Extensions.Hosting`. It uses the default
thread pool. Kestrel handles gRPC connections on its own I/O thread pool.

### Kestrel gRPC handler threads
gRPC service methods run on Kestrel's request handling threads. These threads:
- Must NOT block (no sync I/O)
- Must NOT deadlock with the main host
- Receive `ServerCallContext` with cancellation support

### Service singletons and thread-safety
All singleton services (`VsdbgManager`, `DeploymentManager`, `ProcessLifecycleService`)
may be called concurrently from multiple gRPC requests. They protect mutable state with:
- `SemaphoreSlim(1, 1)` for exclusive operations (install vsdbg, commit deployment)
- `ConcurrentDictionary<K,V>` for concurrent-read, occasional-write collections
- `Interlocked` for counters

### ProcessLifecycleService background watchers
Each tracked process has a background task:
```csharp
_ = Task.Run(async () => {
    await process.WaitForExitAsync(linkedCts.Token);
    ProcessExited?.Invoke(this, new ProcessExitedEventArgs(pid, process.ExitCode));
    _tracked.TryRemove(pid, out _);
}, CancellationToken.None); // intentionally CancellationToken.None to not cancel the watcher
```
The watcher uses `CancellationToken.None` because it must detect process exit even if
the original operation was cancelled.

---

## 4. CancellationToken Propagation

### VSIX
Every user-initiated operation has a `CancellationTokenSource` tied to its lifetime:

| Operation | CTS Source | Linked To |
|-----------|-----------|-----------|
| Debug launch | `DebugSessionOrchestrator._sessionCts` | VS stop token |
| File upload | Derived from session CTS | User cancel |
| Agent log stream | Derived from session CTS | Session end |
| Device probe | `TimeoutCancellationTokenSource(30s)` | UI cancel button |

VS provides a `CancellationToken` in `DebugLaunchContext` that fires when the user
stops the session. This is chained into the session CTS via `CancellationTokenSource.CreateLinkedTokenSource()`.

### Agent
gRPC's `ServerCallContext.CancellationToken` fires when the client disconnects.
All service methods pass this token through to every async operation.

For the streaming log RPC, the token is the primary mechanism to terminate the stream:
```csharp
public override async Task StreamLogs(StreamLogsRequest request,
    IServerStreamWriter<LogEvent> responseStream,
    ServerCallContext context)
{
    await foreach (var entry in _logChannel.ReadAllAsync(context.CancellationToken))
    {
        await responseStream.WriteAsync(entry, context.CancellationToken);
    }
}
```

---

## 5. Async State Machine Discipline

### No fire-and-forget in VSIX
All tasks in VSIX are awaited or explicitly tracked. Any background operation that must
outlive its initiating scope is tracked in `_backgroundTasks` and awaited on shutdown.

### Agent host shutdown
The agent's `IHostedService` implementations:
- `StartAsync`: must return quickly (start background work, don't block)
- `StopAsync`: awaits all in-flight operations (vsdbg session cleanup, port closure)
- Shutdown timeout: 30 seconds (configurable in `appsettings.json`)

### Deadlock prevention
The VSIX never calls gRPC from the VS UI thread. The sequence is always:
```
UI thread event
  → SwitchToBackground()
    → gRPC call (awaited)
      → result
    → SwitchToUIThread() (only if VS API needed)
      → VS API call
```

There are no waits in the reverse direction (background waiting on UI thread completion)
except through `JoinableTaskFactory.RunAsync`, which is deadlock-safe.

---

## 6. Connection State Events and UI Updates

The `SshConnectionManager` raises `ConnectionStateChanged` events on a thread pool thread.
VSIX subscribers marshal these to the UI thread:

```csharp
_connectionManager.ConnectionStateChanged += async (s, e) =>
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
    UpdateStatusBar(e.NewState);
};
```

Device list `INotifyCollectionChanged` events from `DeviceRegistry` are also marshaled
to the UI thread before raising, so WPF data binding works without explicit marshaling
in the ViewModels.
