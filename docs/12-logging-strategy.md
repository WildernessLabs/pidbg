# PiDbg — Logging Strategy

---

## 1. Logging Philosophy

- All logs are structured (key-value properties, not string interpolation into a single message)
- Correlation IDs tie VSIX-side and agent-side events for a single operation
- Developers see concise progress in the VS Output window; full detail in files
- No sensitive data in logs (SSH credentials, key material)
- Agent logs rotate automatically — no unbounded disk growth

---

## 2. VSIX Logging

### Serilog configuration
```csharp
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Grpc", LogEventLevel.Warning)   // suppress gRPC internals
    .MinimumLevel.Override("System.Net", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Component", "VSIX")
    .WriteTo.Sink(new VsOutputWindowSink(outputWindowService))  // → VS Output pane
    .WriteTo.File(
        Path.Combine(LocalAppData, "PiDbg", "logs", "pidbg-vsix-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
    .CreateLogger();
```

### VS Output Window Sink
Formats log entries for developer consumption:
- `Debug`: not shown in Output window (file only)
- `Information`: shown with no level prefix
- `Warning`: shown with `[WARN]` prefix
- `Error`: shown with `[ERROR]` prefix, guidance message if available

```
[PiDbg] Connecting to raspberrypi (192.168.1.100:22)...
[PiDbg] Connected. Agent version 1.1.0.
[PiDbg] Building MyApp (Debug, linux-arm64)...
[PiDbg] Build succeeded.
[PiDbg] Deploying: 15 files, 4.1 MB
[PiDbg] Uploading... [████████░░] 8/15 files (2.1 MB / 4.1 MB)
[PiDbg] Deployment committed.
[PiDbg] Starting vsdbg 17.x on port 4024...
[PiDbg] Debugger attached to MyApp (PID 4829)
```

### Correlation IDs
Every F5 press generates a new `Guid` as `SessionCorrelationId`. This is:
- Pushed to `LogContext` for all VSIX log entries
- Sent in `StartSessionRequest.CorrelationId` to the agent
- Included in every agent log entry for that session
- Enables cross-component tracing in the log files

```csharp
using (LogContext.PushProperty("CorrelationId", sessionId.ToString("N")[..8]))
{
    // All log calls within this scope include CorrelationId
    await DeployAsync(...);
    await AttachDebuggerAsync(...);
}
```

---

## 3. Agent Logging

### Serilog configuration
```csharp
builder.Host.UseSerilog((ctx, services, config) =>
{
    config
        .ReadFrom.Configuration(ctx.Configuration)   // appsettings.json overrides
        .MinimumLevel.Information()
        .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
        .MinimumLevel.Override("Grpc.AspNetCore", LogEventLevel.Warning)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Component", "Agent")
        .Enrich.WithProperty("AgentVersion", AgentVersion.Current)
        .WriteTo.Console(
            formatter: new CompactJsonFormatter())   // systemd journal captures stdout
        .WriteTo.File(
            formatter: new CompactJsonFormatter(),
            path: "/opt/pidbg/logs/pidbg-agent-.log",
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 5,
            fileSizeLimitBytes: 10 * 1024 * 1024,    // 10 MB per file
            rollOnFileSizeLimit: true);
});
```

### Structured log examples
```json
{"@t":"2025-01-15T10:23:45.123Z","@l":"Information","@m":"Deployment committed",
  "DeploymentId":"abc123","AppName":"MyApp","FilesVerified":15,
  "BytesTotal":4194304,"CorrelationId":"7f3a2b1c","Component":"Agent"}

{"@t":"2025-01-15T10:23:46.456Z","@l":"Information","@m":"vsdbg launched",
  "VsdbgPid":4823,"VsdbgPort":4024,"AppPath":"/opt/pidbg/apps/MyApp/current/MyApp.dll",
  "SessionId":"sess-001","CorrelationId":"7f3a2b1c","Component":"Agent"}
```

### Log streaming to VSIX
The agent streams log events to the VSIX in real-time via the `StreamLogs` gRPC RPC.

A custom Serilog sink writes to an in-memory `Channel<LogEvent>`:
```csharp
public class GrpcLogStreamSink : ILogEventSink
{
    private readonly Channel<LogEvent> _channel =
        Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest  // never block on full channel
        });

    public void Emit(LogEvent logEvent)
    {
        _channel.Writer.TryWrite(logEvent.ToProto());
    }

    public IAsyncEnumerable<LogEvent> ReadAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}
```

The `StreamLogs` RPC drains this channel and writes to the gRPC response stream.
When the client disconnects, the `ServerCallContext.CancellationToken` fires and the
`ReadAllAsync` loop exits cleanly.

Filter: by default only `Information` and above are streamed. `Debug` level is file-only
(reduces noise in VS Output window). Configurable per session.

---

## 4. vsdbg Log Integration

vsdbg writes its own internal engine log to:
```
/opt/pidbg/logs/vsdbg-engine-<sessionId>.log
```

This log is invaluable for diagnosing attach failures, missing symbols, etc. The VSIX
surfaces it in two ways:
1. A "Show vsdbg log" button in the Output window toolbar (when a session is active)
2. Included in "Collect Diagnostic Info" export (see §6)

The agent tails this file and streams it via a separate `StreamVsdbgLog` RPC (Phase 2).
In Phase 1, the user can SSH in and view it manually — the Output window shows the path.

---

## 5. Log Levels by Operation

| Operation | Level | Notes |
|-----------|-------|-------|
| SSH connect attempt | Information | |
| SSH connect success | Information | Include latency |
| gRPC Ping | Debug | Too frequent to show |
| Build start/end | Information | |
| Deployment start | Information | Include file count + size |
| File upload | Debug | Per-file; too noisy for Information |
| Deployment commit | Information | |
| SHA-256 mismatch | Error | Include file name and hashes |
| vsdbg install start | Information | |
| vsdbg launch | Information | Include PID and port |
| Debugger attached | Information | Include app PID |
| Session ended | Information | Include duration |
| Any retry | Warning | Include attempt number |
| Network error | Warning | |
| Authentication failure | Error | |
| Internal error | Error | Full stack trace to file |

---

## 6. Diagnostic Collection

"Collect Diagnostic Info" command (Device Manager context menu) gathers:
1. Last 1000 lines from VSIX log file
2. Last 1000 lines from agent log file (fetched via SFTP)
3. Agent status output (OS info, .NET version, disk space, vsdbg version)
4. vsdbg engine log if a session was active
5. SSH connection latency sample

Output: `.zip` file dropped to Desktop. User shares this with support.
