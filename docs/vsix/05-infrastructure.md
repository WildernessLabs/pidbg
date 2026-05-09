# PiDbg VSIX — Infrastructure, Credentials, Logging, Telemetry

---

## 1. Credential Storage

All secrets are stored in the **Windows Credential Manager** — never in `devices.json`,
never in the registry, never in source control.

### Credential types

| Credential | Manager key | Notes |
|---|---|---|
| SSH password | `PiDbg/password/{deviceId}` | Stored as generic credential |
| SSH key passphrase | `PiDbg/passphrase/{deviceId}` | Stored as generic credential |

SSH private key files are stored at a user-specified path (default `%USERPROFILE%\.pidbg\keys\`).
The path is stored in `DeviceRecord.SshKeyPath` — not the key material itself.

### ICredentialService

```csharp
public interface ICredentialService
{
    // Returns null if no credential stored (first use, or user declined to save)
    Task<string?> GetPasswordAsync(Guid deviceId, CancellationToken ct);
    Task StorePasswordAsync(Guid deviceId, string password, CancellationToken ct);
    Task DeletePasswordAsync(Guid deviceId, CancellationToken ct);

    Task<string?> GetPassphraseAsync(Guid deviceId, CancellationToken ct);
    Task StorePassphraseAsync(Guid deviceId, string passphrase, CancellationToken ct);
    Task DeletePassphraseAsync(Guid deviceId, CancellationToken ct);
}
```

### Implementation

Uses `AdysTech.CredentialManager` NuGet package, which P/Invokes `CredRead`/`CredWrite`
from `advapi32.dll`:

```csharp
internal sealed class WindowsCredentialService : ICredentialService
{
    public Task<string?> GetPasswordAsync(Guid deviceId, CancellationToken ct)
    {
        var cred = CredentialManager.GetCredentials($"PiDbg/password/{deviceId}");
        return Task.FromResult(cred?.Password);
    }

    public Task StorePasswordAsync(Guid deviceId, string password, CancellationToken ct)
    {
        CredentialManager.SaveCredentials(
            $"PiDbg/password/{deviceId}",
            new NetworkCredential(string.Empty, password));
        return Task.CompletedTask;
    }
    // ...
}
```

All calls are wrapped in `Task.FromResult` / synchronous — Credential Manager calls are
fast local operations (< 1ms). No async wrapping is needed.

### On device deletion
`RemoveDeviceAsync` in `DeviceRegistry` also calls `ICredentialService.DeletePasswordAsync`
and `DeletePassphraseAsync` to clean up stored credentials. No orphaned credentials remain.

---

## 2. Logging

### Serilog configuration
Configured in `DiContainerBuilder.Build()`:

```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Grpc", LogEventLevel.Warning)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "VSIX")
    .WriteTo.Sink(new VsOutputWindowSink(outputWindowService),
        restrictedToMinimumLevel: LogEventLevel.Information)
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiDbg", "logs", "pidbg-vsix-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate:
            "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext} " +
            "{CorrelationId} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

### VsOutputWindowSink
The VS Output pane receives `Information` and above. Each log entry is formatted for
developer readability (not JSON):

```
[PiDbg] 10:23:44  Connecting to Dev Board (192.168.1.100:22)...
[PiDbg] 10:23:45  Connected. Agent 1.1.0, .NET 10.0.1
[PiDbg] 10:23:45  Building MyApp (Debug, linux-arm64)...
[PiDbg] 10:23:47  Build succeeded.
[PiDbg] 10:23:47  Deploying: 15 files, 4.1 MB
[PiDbg] 10:23:49  Uploaded 15/15 files (4.1 MB) in 1.8s
[PiDbg] 10:23:49  Deployment committed.
[PiDbg] 10:23:50  vsdbg started (PID 4823, port 4024)
[PiDbg] 10:23:50  Debugger attached — MyApp (PID 4829)
```

The `VsOutputWindowSink` marshals writes to the VS UI thread via
`ThreadHelper.JoinableTaskFactory`:

```csharp
internal sealed class VsOutputWindowSink : ILogEventSink
{
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Information) return;
        var text = FormatForOutput(logEvent);
        _package.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _pane.OutputStringThreadSafe(text);
        });
    }
}
```

Note: `OutputStringThreadSafe` is available on `IVsOutputWindowPane` — it internally
marshals to the UI thread. The `SwitchToMainThreadAsync` above is therefore redundant
but serves as explicit documentation.

### Correlation IDs
Every F5 press generates an 8-character hex session ID that is included in every log
entry for that session. This makes it trivial to find all entries for a specific session
in the log file:

```csharp
// In DebugSessionOrchestrator:
var sessionId = Guid.NewGuid().ToString("N")[..8];
using (LogContext.PushProperty("CorrelationId", sessionId))
{
    // All log calls here include CorrelationId
    await DeployAsync(...);
    await AttachAsync(...);
}
```

---

## 3. Threading Model

### Rules (absolute)
1. **Never call VS SDK APIs off the UI thread** — symptoms: COM exceptions, hangs
2. **Never block the UI thread** — no `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`
3. **ConfigureAwait(false) everywhere in library code** — prevents context capture
4. **JoinableTaskFactory.RunAsync for fire-and-forget** — prevents deadlocks via JTC

### Switch patterns

```csharp
// Switching TO background (before any I/O):
await TaskScheduler.Default;

// Switching TO UI thread (before any VS API call):
await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

// Fire-and-forget that may touch VS:
_package.JoinableTaskFactory.RunAsync(async () =>
{
    await SomeLongOperationAsync(ct);
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
    UpdateStatusBar("Done");
}).FireAndForget(); // from Community.VisualStudio.Toolkit
```

### JoinableTaskContext
The `JoinableTaskContext` owned by VS is the source of truth for UI thread ownership.
Do not create a new `JoinableTaskContext` — always use `ThreadHelper.JoinableTaskFactory`
or `package.JoinableTaskFactory`.

### ObservableCollection updates
WPF `ObservableCollection<T>` must be updated on the UI thread. For collections bound
in the Device Manager or Log Viewer, use `DispatcherQueue.TryEnqueue()`:

```csharp
// Called from background thread (gRPC log stream consumer):
_dispatcherQueue.TryEnqueue(() => LogEntries.Add(new LogEventViewModel(entry)));
```

Max collection size is enforced in the ViewModel before Add — removes from the front
when over limit (circular buffer behaviour for Log Viewer).

---

## 4. Telemetry Hooks

Telemetry is opt-in and uses VS's built-in telemetry infrastructure.

### ITelemetryService interface

```csharp
public interface ITelemetryService
{
    void TrackEvent(string eventName,
        IReadOnlyDictionary<string, object>? properties = null);

    void TrackException(Exception ex,
        IReadOnlyDictionary<string, object>? properties = null);

    void TrackDuration(string operationName,
        TimeSpan duration,
        bool success,
        IReadOnlyDictionary<string, object>? properties = null);
}
```

### Events tracked

| Event name | Properties | Notes |
|---|---|---|
| `PiDbg/SessionStarted` | `duration_connect_ms`, `duration_deploy_ms`, `duration_attach_ms`, `delta_files`, `delta_bytes` | Full F5 timing |
| `PiDbg/SessionEnded` | `duration_session_ms`, `exit_reason` | `exit_reason`: "user_stop", "app_exit", "connection_lost" |
| `PiDbg/DeployCompleted` | `file_count`, `bytes`, `duration_ms`, `delta` | |
| `PiDbg/VsdbgInstalled` | `version`, `duration_ms` | |
| `PiDbg/ErrorOccurred` | `error_code`, `phase` | NO stack traces, NO paths |
| `PiDbg/DeviceAdded` | `auth_method` | `"key"` or `"password"` |

**Privacy**: No hostnames, IP addresses, file paths, usernames, or project names are
ever sent to telemetry.

### VS Telemetry implementation

```csharp
internal sealed class VsTelemetryService : ITelemetryService
{
    private readonly TelemetrySession _session = TelemetryService.DefaultSession;

    public void TrackEvent(string eventName,
        IReadOnlyDictionary<string, object>? properties = null)
    {
        var evt = new TelemetryEvent($"vs/pidbg/{eventName}");
        if (properties != null)
            foreach (var (k, v) in properties)
                evt.Properties[$"vs.pidbg.{k}"] = v;
        _session.PostEvent(evt);
    }
}
```

### Null implementation (telemetry disabled)
When the user has opted out of VS telemetry, `TelemetryService.DefaultSession.IsOptedIn`
is false. The DI container registers `NullTelemetryService` in this case:

```csharp
// In DiContainerBuilder:
services.AddSingleton<ITelemetryService>(
    TelemetryService.DefaultSession.IsOptedIn
        ? new VsTelemetryService()
        : NullTelemetryService.Instance);
```

---

## 5. Extension Lifetime Events

### Solution open/close
When a solution opens, the VSIX checks if any project has a Raspberry Pi profile
and updates the Device Manager if open:

```csharp
_solutionEvents.OnAfterOpenProject += (_, _) =>
    _package.JoinableTaskFactory.RunAsync(() => RefreshDeviceStatusAsync(ct));
```

### VS shutdown
On `Package.Dispose()` (VS closing):
1. Cancel the package `CancellationTokenSource`
2. `DeviceConnectionFactory.CloseAllConnectionsAsync()` — clean SSH disconnect
3. Flush Serilog (`Log.CloseAndFlush()`)
4. Dispose DI container (`((IDisposable)DiContainer).Dispose()`)

SSH connections are closed cleanly — the Pi will see a proper SSH session termination
rather than a TCP reset. This allows vsdbg (if running) to shut down gracefully.

---

## 6. NuGet Package Reference

### VSIX project (net472)

```xml
<!-- VS SDK -->
<PackageReference Include="Microsoft.VisualStudio.SDK" Version="17.12.*" />
<PackageReference Include="Microsoft.VisualStudio.ProjectSystem.SDK" Version="17.12.*" />
<PackageReference Include="Community.VisualStudio.Toolkit" Version="17.*" />

<!-- gRPC — uses WinHttpHandler for HTTP/2 on net472 -->
<PackageReference Include="Grpc.Net.Client" Version="2.*" />
<PackageReference Include="Grpc.Net.Client.Web" Version="2.*" />
<PackageReference Include="Google.Protobuf" Version="3.*" />

<!-- SSH -->
<PackageReference Include="SSH.NET" Version="2024.*" />

<!-- DI -->
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.Logging" Version="8.*" />
<PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="8.*" />

<!-- Logging -->
<PackageReference Include="Serilog" Version="4.*" />
<PackageReference Include="Serilog.Extensions.Logging" Version="8.*" />
<PackageReference Include="Serilog.Sinks.File" Version="6.*" />

<!-- Retry -->
<PackageReference Include="Polly" Version="8.*" />

<!-- Credentials -->
<PackageReference Include="AdysTech.CredentialManager" Version="2.*" />

<!-- MVVM (WPF bindings) -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.*" />
```

### gRPC on net472 — WinHttpHandler note
`Grpc.Net.Client` requires HTTP/2. On net472, the default `HttpClientHandler` does not
support HTTP/2. Use `WinHttpHandler` (available on Windows 10+, which VS 2026 requires):

```csharp
var channel = GrpcChannel.ForAddress($"http://localhost:{localPort}",
    new GrpcChannelOptions
    {
        HttpHandler = new WinHttpHandler
        {
            EnableMultipleHttp2Connections = true
        }
    });
```

`WinHttpHandler` is available via `System.Net.Http.WinHttpHandler` NuGet package
(already pulled in transitively by several MS packages).

---

## 7. Recommended VS SDK APIs

| Task | API |
|---|---|
| Get VS service | `AsyncPackage.GetServiceAsync<T>()` |
| UI thread access | `ThreadHelper.JoinableTaskFactory` |
| Output window | `IVsOutputWindow` → `IVsOutputWindowPane` |
| Status bar | `IVsStatusbar` |
| Info bar | `IVsInfoBarUIFactory` + `IVsInfoBarHost` |
| Error list | `ErrorListProvider` |
| Solution info | `IVsSolution`, `IVsProject` |
| Build | `IBuildManager` (CPS) |
| Debug launch | `IVsDebugger4` from `SVsShellDebugger` |
| Debug events | `IVsDebugger.AdviseDebuggerEvents()` |
| Shell open document | `IVsUIShellOpenDocument` |
| Settings store | `ISettingsManager` (VS settings, not device config) |
| File change notification | `IVsFileChangeEx` |
| VS images | `IVsImageService2` + `KnownMonikers` |
