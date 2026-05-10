using Grpc.Core;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Services;

namespace Meadow.Daemon.GrpcService;

internal sealed class MeadowDaemonGrpcService : MeadowDaemonService.MeadowDaemonServiceBase
{
    private readonly DeploymentManager _deploymentManager;
    private readonly ProcessManager _processManager;
    private readonly VsdbgManager _vsdbgManager;
    private readonly DebugSessionManager _debugSessionManager;
    private readonly LogEventChannel _logChannel;
    private readonly DaemonOptions _opts;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MeadowDaemonGrpcService> _log;

    private static readonly global::Meadow.Daemon.Contracts.V1.DaemonVersion s_version = new()
    {
        Version = "0.1.0-dev",
        ProtocolVersion = 1,
        GitCommit = "dev",
    };

    public MeadowDaemonGrpcService(
        DeploymentManager deploymentManager,
        ProcessManager processManager,
        VsdbgManager vsdbgManager,
        DebugSessionManager debugSessionManager,
        LogEventChannel logChannel,
        DaemonOptions opts,
        IHostApplicationLifetime lifetime,
        ILogger<MeadowDaemonGrpcService> log)
    {
        _deploymentManager = deploymentManager;
        _processManager = processManager;
        _vsdbgManager = vsdbgManager;
        _debugSessionManager = debugSessionManager;
        _logChannel = logChannel;
        _opts = opts;
        _lifetime = lifetime;
        _log = log;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    public override Task<PongResponse> Ping(PingRequest request, ServerCallContext ctx)
        => Task.FromResult(new PongResponse
        {
            Version = s_version,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    public override Task<global::Meadow.Daemon.Contracts.V1.DaemonVersion> GetDaemonVersion(
        PingRequest request, ServerCallContext ctx)
        => Task.FromResult(s_version);

    public override Task<DeviceInfo> GetDeviceInfo(GetDeviceInfoRequest request, ServerCallContext ctx)
        => Task.FromResult(new DeviceInfo
        {
            Hostname      = System.Net.Dns.GetHostName(),
            Architecture  = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLower(),
            OsVersion     = System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion(),
            DotnetVersion = Environment.Version.ToString(),
            UptimeSeconds = Environment.TickCount64 / 1000,
            MachineId     = ReadMachineId(),
        });

    public override Task<HealthStatus> GetHealth(PingRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 2");

    public override Task StreamHealth(PingRequest request,
        IServerStreamWriter<HealthStatus> stream, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 2");

    public override Task StreamLogs(StreamLogsRequest request,
        IServerStreamWriter<LogEvent> stream, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 2");

    // ── Process Lifecycle ────────────────────────────────────────────────────

    public override Task<StartApplicationResponse> StartApplication(
        StartApplicationRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<StopApplicationResponse> StopApplication(
        StopApplicationRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<RestartApplicationResponse> RestartApplication(
        RestartApplicationRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<GetApplicationStatusResponse> GetApplicationStatus(
        GetApplicationStatusRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<ListProcessesResponse> ListProcesses(
        ListProcessesRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task StreamOutput(StreamOutputRequest request,
        IServerStreamWriter<OutputLine> stream, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    // ── Deployment ───────────────────────────────────────────────────────────

    public override Task<BeginDeploymentResponse> BeginDeployment(
        BeginDeploymentRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<CommitDeploymentResponse> CommitDeployment(
        CommitDeploymentRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<AbortDeploymentResponse> AbortDeployment(
        AbortDeploymentRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<ListDeploymentsResponse> ListDeployments(
        ListDeploymentsRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<GetCurrentManifestResponse> GetCurrentManifest(
        GetCurrentManifestRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<SetActiveVersionResponse> SetActiveVersion(
        SetActiveVersionRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<DeleteVersionResponse> DeleteVersion(
        DeleteVersionRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    public override Task<PruneDeploymentsResponse> PruneDeployments(
        PruneDeploymentsRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 3");

    // ── vsdbg Management ─────────────────────────────────────────────────────

    public override Task<GetVsdbgInfoResponse> GetVsdbgInfo(
        GetVsdbgInfoRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<InstallVsdbgResponse> InstallVsdbg(
        InstallVsdbgRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<UploadVsdbgTarballResponse> UploadVsdbgTarball(
        UploadVsdbgTarballRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    // ── Debug Sessions ───────────────────────────────────────────────────────

    public override Task<StartDebugSessionResponse> StartDebugSession(
        StartDebugSessionRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<StopDebugSessionResponse> StopDebugSession(
        StopDebugSessionRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<GetSessionStatusResponse> GetSessionStatus(
        GetSessionStatusRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    public override Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 5");

    // ── Self-Update ──────────────────────────────────────────────────────────

    public override Task<PrepareUpdateResponse> PrepareUpdate(
        PrepareUpdateRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 7");

    public override Task<ApplyUpdateResponse> ApplyUpdate(
        ApplyUpdateRequest request, ServerCallContext ctx)
        => throw new NotImplementedException("Implemented in Phase 7");

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ReadMachineId()
    {
        try { return File.ReadAllText("/etc/machine-id").Trim(); }
        catch { return ""; }
    }
}
