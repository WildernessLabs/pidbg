# Phase 2 — Daemon Skeleton, gRPC Contracts, Health Endpoints

All tasks in this phase depend on Phase 1 being complete. P2.2 and P2.3 can be worked in
parallel once P2.1 is verified. P2.4–P2.8 all depend on P2.3.

---

## P2.1 — Proto Compilation Verification

**Purpose**: Confirm that all five proto files generate correct, complete C# types so that
downstream tasks can import and use them without guesswork.

**Dependencies**: P1.3

**Files**:
- `Source/Meadow.Daemon.Contracts/proto/common.proto`
- `Source/Meadow.Daemon.Contracts/proto/deployment.proto`
- `Source/Meadow.Daemon.Contracts/proto/process.proto`
- `Source/Meadow.Daemon.Contracts/proto/session.proto`
- `Source/Meadow.Daemon.Contracts/proto/meadow_daemon.proto`

**Implementation details**:

Run `dotnet build Source/Meadow.Daemon.Contracts` and verify `obj/Debug/net10.0/` contains:
```
CommonReflection.cs          (from common.proto)
DeploymentReflection.cs      (from deployment.proto)
ProcessReflection.cs         (from process.proto)
SessionReflection.cs         (from session.proto)
MeadowDaemonReflection.cs    (from meadow_daemon.proto)
MeadowDaemonGrpc.cs          (unified gRPC stubs from meadow_daemon.proto)
```

Required proto message types — verify each exists in generated output:

`common.proto` must generate:
- `PingRequest`, `PongResponse`
- `DaemonVersion`, `DeviceInfo`
- `HealthState` enum, `HealthStatus`, `AppHealthItem`, `VsdbgInfo`
- `LogLevel` enum, `LogEvent`, `StreamLogsRequest`

`deployment.proto` must generate:
- `DeploymentSlot` enum (`UnspecifiedSlot`, `Production`, `Debug`)
- `FileEntry` (path, sha256, size_bytes, role)
- `DeploymentManifest` (manifest_version, deployment_id, slot, version_label,
  entry_point, files, manifest_sha256)
- `BeginDeploymentRequest` / `BeginDeploymentResponse`
- `CommitDeploymentRequest` / `CommitDeploymentResponse`
- `AbortDeploymentRequest` / `AbortDeploymentResponse`
- `ListDeploymentsRequest` / `ListDeploymentsResponse`
- `GetCurrentManifestRequest` / `GetCurrentManifestResponse`
- `SetActiveVersionRequest` / `SetActiveVersionResponse`
- `DeleteVersionRequest` / `DeleteVersionResponse`
- `PruneDeploymentsRequest` / `PruneDeploymentsResponse`

`process.proto` must generate:
- `AppState` enum (`Unknown`, `Starting`, `Running`, `Stopping`, `Stopped`, `Failed`)
- `ApplicationStatus`, `OutputStream` enum, `OutputLine`
- `StartProcessRequest` / `StartProcessResponse`
- `StopProcessRequest` / `StopProcessResponse`
- `RestartProcessRequest` / `RestartProcessResponse`
- `GetProcessStatusRequest` / `GetProcessStatusResponse`
- `ListProcessesRequest` / `ListProcessesResponse`
- `StreamOutputRequest` / no response (server streaming)

`session.proto` must generate:
- `SessionMode` enum (`Attach`, `Launch`)
- `SessionState` enum (`Starting`, `Ready`, `Stopping`, `Stopped`, `Failed`)
- `SessionStatus`
- `GetVsdbgInfoRequest` / `GetVsdbgInfoResponse`
- `InstallVsdbgRequest` / `InstallVsdbgProgress` (streaming)
- `UploadVsdbgTarballRequest` (client streaming) / `UploadVsdbgTarballResponse`
- `StartDebugSessionRequest` / `StartDebugSessionResponse`
- `StopDebugSessionRequest` / `StopDebugSessionResponse`
- `GetSessionStatusRequest` / `GetSessionStatusResponse`
- `ListSessionsRequest` / `ListSessionsResponse`

`meadow_daemon.proto` must generate:
- `PrepareUpdateRequest`, `PrepareUpdateChunk` (client streaming), `PrepareUpdateResponse`
- `ApplyUpdateRequest`, `ApplyUpdateResponse`
- `GetDeviceInfoRequest` / `GetDeviceInfoResponse`
- `MeadowDaemonService.MeadowDaemonServiceBase` (abstract class, all RPCs virtual)
- `MeadowDaemonService.MeadowDaemonServiceClient` (concrete client class)

Fix any proto syntax errors found. Common issues from design-phase proto files:
- Missing `import "google/protobuf/timestamp.proto"` for timestamp fields
- `map<string,string>` requires proto3 and is valid but the C# type is
  `Google.Protobuf.Collections.MapField<string, string>`
- Enum values in proto3 must have a zero-value first entry (e.g. `UNSPECIFIED = 0`)
- `oneof` fields and `optional` keyword behave differently in proto3 vs proto2

**Edge cases**:
- If `meadow_daemon.proto` imports the other proto files and those imports fail, the
  entire service is ungenerated. Fix import paths before other tasks proceed.
- Proto enum values that conflict with C# keywords (e.g. `Error`) generate as `Error_`
  in C#. Rename proto enum values to avoid this: use `AppStateError` not `Error`.
- `StreamOutputRequest` has no response message because it uses server-side streaming.
  The proto definition should be `rpc StreamOutput (StreamOutputRequest) returns (stream OutputLine)`.

**Testing requirements**:
- `dotnet build` exits 0 with zero errors
- `grep -r "MeadowDaemonServiceBase" $(find . -name "*.cs" -path "*/obj/*")` finds result
- Manually inspect generated `MeadowDaemonGrpc.cs` — verify all RPCs from
  `meadow_daemon.proto` service block appear as abstract methods in `ServiceBase`
- Compile a trivial test class that `new`s a `PingRequest()` — confirms types are accessible

**Definition of done**:
- [ ] `dotnet build` on Contracts project exits 0 with zero errors and zero warnings
- [ ] All message types listed above exist in generated C#
- [ ] `MeadowDaemonServiceBase` has abstract methods for all RPCs
- [ ] `MeadowDaemonServiceClient` exists and is constructible

> **Status**: Not verified — requires `dotnet build` on real Linux/ARM64 target.

---

## P2.2 — Program.cs Host Builder

**Purpose**: Wire all services, middleware, and Kestrel endpoints into a runnable host
so that `meadow-daemon` starts, binds its ports, and exits cleanly on SIGTERM.

**Dependencies**: P1.4, P1.6, P1.7, P1.8, P1.9, P2.1

**Files**:
- `Source/Meadow.Daemon/Program.cs` (already exists as stub — complete it)

**Implementation details**:

```csharp
var builder = WebApplication.CreateBuilder(args);

// --- Configuration ---
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("/etc/meadow/daemon.conf", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "MEADOW_");

// --- Options ---
builder.Services
    .AddOptions<DaemonOptions>()
    .BindConfiguration(DaemonOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// --- Logging ---
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    o.UseUtcTimestamp = true;
});
builder.Services.AddSingleton<LogEventChannel>();
builder.Logging.Services.AddSingleton<ILoggerProvider, LogEventLoggerProvider>();

// --- systemd ---
builder.Host.UseSystemd();

// --- Domain services (registered but not yet implemented — P3/P5/P7) ---
builder.Services.AddSingleton<StateStore>();
// Placeholder registrations; implementations come in later phases:
// builder.Services.AddSingleton<IDeploymentManager, DeploymentManager>();
// builder.Services.AddSingleton<IProcessManager, ProcessManager>();
// builder.Services.AddSingleton<IVsdbgManager, VsdbgManager>();
// builder.Services.AddSingleton<IDebugSessionManager, DebugSessionManager>();

// --- gRPC ---
builder.Services.AddGrpc(o =>
{
    o.EnableDetailedErrors = builder.Environment.IsDevelopment();
    o.MaxReceiveMessageSize = 64 * 1024 * 1024;  // 64 MB for binary uploads
    o.MaxSendMessageSize    = 64 * 1024 * 1024;
});
builder.Services.AddGrpcReflection();
builder.Services.AddGrpcHealthChecks();

// --- REST (compat) ---
builder.Services.AddControllers();

// --- Kestrel ---
builder.WebHost.ConfigureKestrel((ctx, opts) =>
{
    var daemon = ctx.Configuration.GetSection(DaemonOptions.Section);
    var grpcPort = daemon.GetValue<int>("GrpcPort", 50051);
    var restPort = daemon.GetValue<int>("RestPort",  5000);

    // gRPC endpoint: HTTP/2 only, no TLS (SSH tunnel provides encryption)
    opts.ListenLocalhost(grpcPort, lo =>
    {
        lo.Protocols = HttpProtocols.Http2;
    });

    // REST compat endpoint: HTTP/1.1 only
    opts.ListenLocalhost(restPort, lo =>
    {
        lo.Protocols = HttpProtocols.Http1;
    });
});

var app = builder.Build();

// Ensure directories exist before any service starts
var opts = app.Services.GetRequiredService<IOptions<DaemonOptions>>().Value;
DaemonPaths.EnsureDirectories(opts);

// --- Middleware ---
app.MapGrpcService<MeadowDaemonGrpcService>();
app.MapGrpcHealthChecksService();
if (app.Environment.IsDevelopment())
    app.MapGrpcReflectionService();
app.MapControllers();

app.Run();
```

**Edge cases**:
- `ListenLocalhost` binds to `127.0.0.1` only. This is intentional — SSH is the
  external access layer. Do NOT use `ListenAnyIP`.
- `HttpProtocols.Http2` on the gRPC port and `HttpProtocols.Http1` on the REST port.
  If you accidentally set `Http1AndHttp2` on the gRPC port without TLS, .NET will
  refuse to upgrade HTTP/1.1 to HTTP/2 without TLS and gRPC clients will fail.
- `EnableDetailedErrors = IsDevelopment()` leaks stack traces in errors. Must be
  false in production.
- `MaxReceiveMessageSize = 64 MB` is needed for binary file upload RPCs (vsdbg tarball
  upload). The default is 4 MB which is too small.
- `DaemonPaths.EnsureDirectories` must be called BEFORE `app.Run()` but AFTER the
  options are validated. The ordering in the code above is correct.
- `UseSystemd()` on `builder.Host` (not `builder.WebHost`) installs the systemd
  lifetime and enables `sd_notify` signalling.

**Testing requirements**:
- `dotnet run --project Source/Meadow.Daemon` starts without errors
- `grpcurl -plaintext 127.0.0.1:50051 meadow.daemon.v1.MeadowDaemonService/Ping`
  returns a response (or "Unimplemented" — not a connection error)
- `curl http://127.0.0.1:5000/api/v1/health` returns HTTP 200
- Process exits cleanly on Ctrl+C (SIGINT)

**Definition of done**:
- [x] `Program.cs` compiles and the daemon starts
- [x] gRPC port (50051) only accepts HTTP/2
- [x] REST port (5000) only accepts HTTP/1.1
- [x] Both ports bind to `127.0.0.1` (localhost only)
- [x] `DaemonPaths.EnsureDirectories` called at startup
- [x] `UseSystemd()` present on `builder.Host`
- [ ] Clean shutdown on SIGTERM

---

## P2.3 — MeadowDaemonGrpcService Skeleton

**Purpose**: Create a compiling gRPC service class with all RPCs stubbed so that every
subsequent phase can implement its RPCs without touching the class structure.

**Dependencies**: P2.1, P2.2

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs`
  (already exists — verify structure matches spec, fix if needed)

**Implementation details**:

The class must:
- Inherit `MeadowDaemonService.MeadowDaemonServiceBase`
- Have a single constructor (not two constructors as in the original stub)
- Inject: `IOptions<DaemonOptions>`, `StateStore`, `LogEventChannel`,
  `ILogger<MeadowDaemonGrpcService>`, `IHostApplicationLifetime`
- Later phases will add more injected services (e.g. `IDeploymentManager`); add them
  as those phases are implemented

Stub body for every unimplemented RPC:
```csharp
public override Task<FooResponse> SomeRpc(
    FooRequest request, ServerCallContext context)
{
    throw new RpcException(new Status(
        StatusCode.Unimplemented,
        $"{nameof(SomeRpc)} is not yet implemented"));
}
```

Helper method for logging RPC calls (add to reduce boilerplate):
```csharp
private void LogCall(string rpc, ServerCallContext ctx)
    => _logger.LogDebug("gRPC {Rpc} called by {Peer}", rpc, ctx.Peer);
```

The class must have the following method signatures (complete list):

**Diagnostics**:
- `Ping(PingRequest, ServerCallContext) → Task<PongResponse>`
- `GetDeviceInfo(GetDeviceInfoRequest, ServerCallContext) → Task<DeviceInfoResponse>`
- `StreamLogs(StreamLogsRequest, IServerStreamWriter<LogEvent>, ServerCallContext) → Task`
- `StreamHealth(StreamHealthRequest, IServerStreamWriter<HealthStatus>, ServerCallContext) → Task`

**Process lifecycle**:
- `StartProcess(StartProcessRequest, ServerCallContext) → Task<StartProcessResponse>`
- `StopProcess(StopProcessRequest, ServerCallContext) → Task<StopProcessResponse>`
- `RestartProcess(RestartProcessRequest, ServerCallContext) → Task<RestartProcessResponse>`
- `GetProcessStatus(GetProcessStatusRequest, ServerCallContext) → Task<GetProcessStatusResponse>`
- `ListProcesses(ListProcessesRequest, ServerCallContext) → Task<ListProcessesResponse>`
- `StreamOutput(StreamOutputRequest, IServerStreamWriter<OutputLine>, ServerCallContext) → Task`

**Deployment**:
- `BeginDeployment(BeginDeploymentRequest, ServerCallContext) → Task<BeginDeploymentResponse>`
- `CommitDeployment(CommitDeploymentRequest, ServerCallContext) → Task<CommitDeploymentResponse>`
- `AbortDeployment(AbortDeploymentRequest, ServerCallContext) → Task<AbortDeploymentResponse>`
- `ListDeployments(ListDeploymentsRequest, ServerCallContext) → Task<ListDeploymentsResponse>`
- `GetCurrentManifest(GetCurrentManifestRequest, ServerCallContext) → Task<GetCurrentManifestResponse>`
- `SetActiveVersion(SetActiveVersionRequest, ServerCallContext) → Task<SetActiveVersionResponse>`
- `DeleteVersion(DeleteVersionRequest, ServerCallContext) → Task<DeleteVersionResponse>`
- `PruneDeployments(PruneDeploymentsRequest, ServerCallContext) → Task<PruneDeploymentsResponse>`

**vsdbg management**:
- `GetVsdbgInfo(GetVsdbgInfoRequest, ServerCallContext) → Task<GetVsdbgInfoResponse>`
- `InstallVsdbg(InstallVsdbgRequest, IServerStreamWriter<InstallVsdbgProgress>, ServerCallContext) → Task`
- `UploadVsdbgTarball(IAsyncStreamReader<UploadVsdbgTarballRequest>, ServerCallContext) → Task<UploadVsdbgTarballResponse>`

**Debug sessions**:
- `StartDebugSession(StartDebugSessionRequest, ServerCallContext) → Task<StartDebugSessionResponse>`
- `StopDebugSession(StopDebugSessionRequest, ServerCallContext) → Task<StopDebugSessionResponse>`
- `GetSessionStatus(GetSessionStatusRequest, ServerCallContext) → Task<GetSessionStatusResponse>`
- `ListSessions(ListSessionsRequest, ServerCallContext) → Task<ListSessionsResponse>`

**Self-update**:
- `PrepareUpdate(IAsyncStreamReader<PrepareUpdateChunk>, ServerCallContext) → Task<PrepareUpdateResponse>`
- `ApplyUpdate(ApplyUpdateRequest, ServerCallContext) → Task<ApplyUpdateResponse>`

**Edge cases**:
- `IAsyncStreamReader<T>` parameter means client-streaming RPC. The method signature
  differs from unary RPCs — do not accidentally give it a `ServerCallContext` as the
  first parameter.
- `IServerStreamWriter<T>` parameter means server-streaming RPC.
- All `Task` returns (not `Task<T>`) are fire-and-forget streaming RPCs where the
  server sends items and the method returns when streaming is complete.
- The `ServerCallContext.CancellationToken` must be respected in all streaming RPCs.

**Testing requirements**:
- `dotnet build` exits 0
- All RPC method signatures match the generated `ServiceBase` exactly (build would
  fail if they didn't, due to `override`)
- Manual test: call any stubbed RPC via grpcurl and get `StatusCode.Unimplemented`
  (not a connection error or crash)

**Definition of done**:
- [x] Single constructor with all required injected services
- [x] All RPCs listed above present with correct signatures
- [x] All unimplemented RPCs throw `RpcException(Unimplemented)`
- [x] `override` keyword on every method
- [ ] `dotnet build` exits 0

---

## P2.4 — Ping and GetDeviceInfo RPCs

**Purpose**: Implement the two diagnostics RPCs that the VSIX calls after connecting to
verify the daemon is alive and to read the device's identity and daemon version.

**Dependencies**: P2.3

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (extend existing)

**Implementation details**:

`Ping`:
```csharp
public override Task<PongResponse> Ping(PingRequest request, ServerCallContext context)
{
    LogCall(nameof(Ping), context);
    var asm = Assembly.GetExecutingAssembly();
    return Task.FromResult(new PongResponse
    {
        DaemonVersion = new DaemonVersion
        {
            Semver     = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                            ?.InformationalVersion
                         ?? asm.GetName().Version?.ToString() ?? "0.0.0",
            BuildDate  = asm.GetCustomAttribute<AssemblyMetadataAttribute>() // set at build
                            ?.Value ?? "",
        },
        TimestampMs   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        ProtoVersion  = 1,
    });
}
```

`GetDeviceInfo`:
```csharp
public override Task<GetDeviceInfoResponse> GetDeviceInfo(
    GetDeviceInfoRequest request, ServerCallContext context)
{
    LogCall(nameof(GetDeviceInfo), context);
    return Task.FromResult(new GetDeviceInfoResponse
    {
        Info = new DeviceInfo
        {
            Hostname      = Dns.GetHostName(),
            MachineId     = ReadMachineId(),
            Architecture  = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            OsDescription = RuntimeInformation.OSDescription,
            DotnetVersion = RuntimeInformation.FrameworkDescription,
            DaemonVersion = /* same as Ping */ BuildDaemonVersion(),
        }
    });
}

private static string ReadMachineId()
{
    try { return File.ReadAllText("/etc/machine-id").Trim()[..32]; }
    catch { return Guid.NewGuid().ToString("N"); }
}

private static DaemonVersion BuildDaemonVersion()
{
    var asm = Assembly.GetExecutingAssembly();
    return new DaemonVersion
    {
        Semver = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion ?? "0.0.0"
    };
}
```

Add to `.csproj` so `AssemblyInformationalVersion` is populated at build time:
```xml
<PropertyGroup>
  <Version>1.0.0</Version>
  <InformationalVersion>1.0.0+$(SourceRevisionId)</InformationalVersion>
</PropertyGroup>
```

**Edge cases**:
- `/etc/machine-id` may not exist on developer builds (Windows). The `catch` fallback
  generates a random GUID. This is acceptable; machine-id is advisory only.
- `RuntimeInformation.ProcessArchitecture` returns `Arm64` (capital A). The `.ToLowerInvariant()`
  call normalises it to `arm64`. The detection script checks `uname -m` which returns
  `aarch64`. These are the same architecture with different naming conventions.
  Document this mismatch — `arm64` (dotnet) == `aarch64` (Linux kernel).
- `AssemblyInformationalVersion` with `SourceRevisionId` requires the build to set
  `SourceRevisionId` (e.g. via `git describe`). If not set, the version is just `1.0.0+`.
  This is acceptable.

**Testing requirements**:
- Unit test: `Ping` response has `ProtoVersion == 1`
- Unit test: `Ping` response has non-empty `DaemonVersion.Semver`
- Unit test: `GetDeviceInfo` response has non-empty `Hostname`
- Unit test: `ReadMachineId` returns a string of exactly 32 hex characters when
  `/etc/machine-id` is present
- Integration test: `grpcurl -plaintext 127.0.0.1:50051 meadow.daemon.v1.MeadowDaemonService/Ping`
  returns a valid JSON response

**Definition of done**:
- [x] `Ping` returns `ProtoVersion=1`, timestamp, and semver
- [x] `GetDeviceInfo` returns hostname, architecture, OS, dotnet version
- [x] `ReadMachineId` handles missing `/etc/machine-id` gracefully
- [ ] Both RPCs are tested with unit tests
- [ ] Both RPCs work end-to-end via grpcurl

---

## P2.5 — gRPC Health Check Endpoint

**Purpose**: Expose the standard gRPC health check protocol so the VSIX can determine
whether the daemon is ready to accept work (not just running).

**Dependencies**: P2.2

**Files**:
- `Source/Meadow.Daemon/GrpcService/DaemonHealthCheck.cs`
- `Source/Meadow.Daemon/Program.cs` (extend registrations)

**Implementation details**:

`DaemonHealthCheck.cs`:
```csharp
public sealed class DaemonHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        // Phase 2: always healthy once started.
        // Phase 7: add real checks (state store readable, vsdbg dir exists, etc.)
        return Task.FromResult(HealthCheckResult.Healthy("Meadow Daemon ready"));
    }
}
```

In `Program.cs`, add after `AddGrpcHealthChecks()`:
```csharp
builder.Services
    .AddHealthChecks()
    .AddCheck<DaemonHealthCheck>("daemon");

// In the app pipeline:
app.MapHealthChecks("/health");          // HTTP health endpoint
app.MapGrpcHealthChecksService();        // gRPC health protocol
```

The gRPC health check service name for the VSIX to query is the empty string `""`
(overall service health) or `"meadow.daemon.v1.MeadowDaemonService"` (specific service).

Configure default health for all services:
```csharp
builder.Services.Configure<GrpcHealthChecksOptions>(o =>
{
    o.Services.MapService<MeadowDaemonGrpcService>(
        _ => true); // always healthy in Phase 2
});
```

**Edge cases**:
- The gRPC health check service (`grpc.health.v1.Health`) is a separate gRPC service
  from `MeadowDaemonService`. It must be mapped with `MapGrpcHealthChecksService()` not
  `MapGrpcService<HealthServiceImpl>`.
- HTTP health endpoint at `/health` is for Docker/Kubernetes style probes. It is not
  used by the VSIX but is useful for manual diagnostics (`curl http://127.0.0.1:5000/health`).
- In Phase 7, `DaemonHealthCheck` will add real checks. Keep it trivial now.

**Testing requirements**:
- `grpcurl -plaintext 127.0.0.1:50051 grpc.health.v1.Health/Check` returns `SERVING`
- `curl http://127.0.0.1:5000/health` returns HTTP 200 `Healthy`
- Both endpoints tested in integration test

**Definition of done**:
- [x] `DaemonHealthCheck` registered and returns `Healthy`
- [x] `MapGrpcHealthChecksService()` in pipeline
- [x] `MapHealthChecks("/health")` in pipeline
- [ ] grpcurl health check returns `SERVING`
- [ ] HTTP `/health` returns 200

---

## P2.6 — systemd Type=notify Integration

**Purpose**: Signal systemd that the daemon is ready after all initialisation completes,
so `systemctl start` blocks correctly and `is-active` transitions from `activating` to
`active (running)` at the right moment.

**Dependencies**: P2.2

**Files**:
- `Source/Meadow.Daemon/Program.cs` (already calls `UseSystemd()` — verify)
- `Source/Meadow.Daemon/systemd/meadow-daemon.service.template` (already exists — verify)

**Implementation details**:

`UseSystemd()` from `Microsoft.Extensions.Hosting.Systemd` does all of the following
automatically — verify these behaviours rather than implementing them manually:

1. **sd_notify(READY=1)**: Called by the hosting infrastructure after all `IHostedService`
   instances complete `StartAsync`. This is the signal to systemd that the service is ready.
   Verify: `systemctl start meadow-daemon` blocks until the daemon is ready, then returns.

2. **sd_notify(STOPPING=1)**: Called when `IHostApplicationLifetime.ApplicationStopping`
   fires. Tells systemd the service is in graceful shutdown.

3. **SIGTERM handler**: `UseSystemd()` installs a SIGTERM handler that calls
   `IHostApplicationLifetime.StopApplication()`. Verify: `systemctl stop meadow-daemon`
   results in a clean exit (exit code 0), not a SIGKILL timeout.

4. **Watchdog**: If `WATCHDOG_USEC` env var is set by systemd (requires `WatchdogSec=` in
   the service file), the hosting infrastructure sends periodic watchdog pings. For Phase 2,
   do not set `WatchdogSec=` in the service template — add it in Phase 7 when the daemon
   is stable.

Service template verification checklist:
```ini
[Service]
Type=notify           ← must be "notify" not "simple" or "forking"
NotifyAccess=main     ← only the main process can send sd_notify
```

Verify `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is set in the template — without it,
`dotnet` on a Pi without ICU libraries throws `TypeInitializationException` on startup.

Add a startup log message immediately before `app.Run()`:
```csharp
var logger = app.Services.GetRequiredService<ILogger<Program>>();
logger.LogInformation("Meadow Daemon {Version} starting on gRPC:{GrpcPort} REST:{RestPort}",
    Assembly.GetExecutingAssembly().GetName().Version,
    opts.GrpcPort, opts.RestPort);
```

**Edge cases**:
- `Type=notify` requires the process to actually send `sd_notify`. If `UseSystemd()` is
  called but the `Systemd` package is not installed, the process starts but systemd
  times out waiting for the READY signal. Verify the package is in the `.csproj`.
- If the daemon starts and immediately crashes before `sd_notify(READY=1)`, systemd
  correctly marks the service as `failed`. This is the right behaviour.
- `UseSystemd()` is a no-op when not running under systemd (e.g., direct terminal
  execution). The daemon starts normally without any errors.

**Testing requirements**:
- On Linux with systemd: `systemctl --user start meadow-daemon` exits within 5 seconds
- `systemctl --user is-active meadow-daemon` returns `active` immediately after start
- `systemctl --user stop meadow-daemon` triggers clean shutdown (exit code 0)
- On Windows/macOS dev machine: `dotnet run` starts without errors (UseSystemd is no-op)

**Definition of done**:
- [x] `UseSystemd()` present in `Program.cs`
- [x] Service template has `Type=notify` and `NotifyAccess=main`
- [x] `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` in service template
- [x] Startup log message emitted before `app.Run()`
- [ ] `systemctl start/stop` lifecycle verified on a real Pi or VM

---

## P2.7 — StreamLogs gRPC RPC

**Purpose**: Implement the `StreamLogs` RPC so the VSIX can subscribe to live daemon logs
over the existing gRPC connection without a separate SSH session.

**Dependencies**: P2.3, P1.9

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (implement RPC)

**Implementation details**:

```csharp
public override async Task StreamLogs(
    StreamLogsRequest request,
    IServerStreamWriter<LogEvent> responseStream,
    ServerCallContext context)
{
    LogCall(nameof(StreamLogs), context);
    var ct = context.CancellationToken;
    var minLevel = MapProtoLevel(request.MinLevel);

    await foreach (var evt in _logChannel.Subscribe(ct))
    {
        // Filter by minimum level
        if (evt.Level < request.MinLevel) continue;

        // Filter by category prefix (empty string = all categories)
        if (!string.IsNullOrEmpty(request.FilterCategory)
            && !evt.Category.StartsWith(request.FilterCategory,
                                        StringComparison.OrdinalIgnoreCase))
            continue;

        try
        {
            await responseStream.WriteAsync(evt, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StreamLogs subscriber write failed; dropping subscriber");
            break;
        }
    }
}
```

`StreamLogsRequest` fields:
- `min_level`: `LogLevel` enum (0 = Trace → send everything, 3 = Info → filter Debug/Trace)
- `filter_category`: string prefix to filter by category name (e.g. `"Meadow.Daemon.Services"`)

**Edge cases**:
- `await foreach` on `_logChannel.Subscribe(ct)` will loop indefinitely until the channel
  is completed or `ct` is cancelled. The client disconnect fires `ct`, ending the loop.
- `responseStream.WriteAsync` can throw `RpcException` or `InvalidOperationException` if
  the client disconnects while we are mid-write. The `catch (Exception)` handles this.
- Do NOT `await` the `LogEvent` write inside the channel write pipeline. The `StreamLogs`
  RPC runs on a separate gRPC thread; writing to `responseStream` is not re-entrant.
  The pattern above (single `await foreach` loop) is safe.
- A slow subscriber does not back-pressure the `LogEventChannel` because the channel
  uses `DropOldest`. If the subscriber can't keep up, it misses old events.

**Testing requirements**:
- Unit test: call `StreamLogs`, write 10 events to `LogEventChannel`, verify all 10
  appear in the stream (in order)
- Unit test: `min_level = Info` filters out Debug-level events
- Unit test: `filter_category = "MyApp"` filters out events from other categories
- Unit test: cancelling the `CancellationToken` terminates the stream cleanly
- Integration test: `grpcurl` streaming call to `StreamLogs` receives live log events

**Definition of done**:
- [x] `StreamLogs` iterates `LogEventChannel.Subscribe`
- [x] Level and category filters applied correctly
- [x] Client disconnect handled without crashing
- [ ] Unit tests pass for filtering
- [ ] Integration test verified on running daemon

> **Fixed**: `LogEventChannel` now maintains a list of per-subscriber bounded channels.
> Each `Subscribe` call gets its own `Channel<LogEvent>` (capacity 1,000, DropOldest).
> `TryWrite` broadcasts to all active subscribers. Unsubscribe is automatic via `finally`
> in the async iterator when the client disconnects or cancels.

---

## P2.8 — REST Compatibility Controller

**Purpose**: Provide minimal HTTP/1.1 endpoints matching the original Rust daemon's REST
API so existing tooling that probes `/api/v1/health` does not break during the transition.

**Dependencies**: P2.2

**Files**:
- `Source/Meadow.Daemon/RestCompat/MeadowRestCompatController.cs`

**Implementation details**:

```csharp
[ApiController]
[Route("api/v1")]
public sealed class MeadowRestCompatController : ControllerBase
{
    private readonly ILogger<MeadowRestCompatController> _logger;

    public MeadowRestCompatController(ILogger<MeadowRestCompatController> logger)
        => _logger = logger;

    // Health probe — used by scripts and existing tooling
    [HttpGet("health")]
    public IActionResult GetHealth()
        => Ok(new { status = "ok", version = GetVersion() });

    // App list stub — returns empty array, original API shape preserved
    [HttpGet("apps")]
    public IActionResult ListApps()
        => Ok(Array.Empty<object>());

    // All write operations redirect to gRPC
    [HttpPost("apps")]
    [HttpDelete("apps/{name}")]
    [HttpPost("apps/{name}/start")]
    [HttpPost("apps/{name}/stop")]
    public IActionResult GrpcOnly()
        => StatusCode(501, new {
            error = "Use gRPC API (port 50051). REST write operations are not supported.",
            grpcService = "meadow.daemon.v1.MeadowDaemonService"
        });

    private static string GetVersion()
        => Assembly.GetExecutingAssembly()
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion ?? "0.0.0";
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddControllers();
// ...
app.MapControllers();
```

**Edge cases**:
- `MapControllers()` must be called after `app.MapGrpcService<>()` — if it is before,
  HTTP/1.1 requests to the gRPC port will be handled by the controller, not gRPC.
  The Kestrel port split (50051 vs 5000) makes this a non-issue architecturally,
  but ordering still matters for correctness.
- Returning `501 Not Implemented` for write operations is preferable to `404` because
  it clearly tells the caller "this path exists conceptually but is not supported here."

**Testing requirements**:
- `curl http://127.0.0.1:5000/api/v1/health` → HTTP 200, body contains `"status":"ok"`
- `curl http://127.0.0.1:5000/api/v1/apps` → HTTP 200, body is `[]`
- `curl -X POST http://127.0.0.1:5000/api/v1/apps` → HTTP 501

**Definition of done**:
- [x] Controller exists and is mapped
- [x] `/api/v1/health` returns 200 with `{"status":"ok","version":"..."}`
- [x] `/api/v1/apps` returns 200 with `[]`
- [x] POST/DELETE/etc. return 501 with gRPC redirect message
- [x] Controller is on REST port (5000) only — not accessible on gRPC port (50051)
