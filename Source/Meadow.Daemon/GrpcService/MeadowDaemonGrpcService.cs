using Grpc.Core;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Services;

namespace Meadow.Daemon.GrpcService;

// Thin dispatch layer — no logic here. All calls delegate to domain services.
internal sealed class MeadowDaemonGrpcService : MeadowDaemonService.MeadowDaemonServiceBase
{
    private readonly DeploymentManager _deploymentManager;
    private readonly ProcessManager _processManager;
    private readonly VsdbgManager _vsdbgManager;
    private readonly DebugSessionManager _debugSessionManager;
    private readonly HealthReporterService _healthReporter;
    private readonly LogEventChannel _logChannel;
    private readonly DaemonOptions _opts;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MeadowDaemonGrpcService> _log;

    private static readonly DaemonVersion CurrentVersion = new()
    {
        Version = ThisAssembly.AssemblyInformationalVersion,
        ProtocolVersion = 1,
        GitCommit = ThisAssembly.GitCommitId[..7],
    };

    public MeadowDaemonGrpcService(
        DeploymentManager deploymentManager,
        ProcessManager processManager,
        VsdbgManager vsdbgManager,
        DebugSessionManager debugSessionManager,
        HealthReporterService healthReporter,
        LogEventChannel logChannel,
        DaemonOptions opts,
        IHostApplicationLifetime lifetime,
        ILogger<MeadowDaemonGrpcService> log)
    {
        _deploymentManager = deploymentManager;
        _processManager = processManager;
        _vsdbgManager = vsdbgManager;
        _debugSessionManager = debugSessionManager;
        _healthReporter = healthReporter;
        _logChannel = logChannel;
        _opts = opts;
        _lifetime = lifetime;
        _log = log;
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────

    public override Task<PongResponse> Ping(PingRequest request, ServerCallContext ctx)
        => Task.FromResult(new PongResponse
        {
            Version = CurrentVersion,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    public override Task<DaemonVersion> GetDaemonVersion(PingRequest request, ServerCallContext ctx)
        => Task.FromResult(CurrentVersion);

    public override async Task<HealthStatus> GetHealth(PingRequest request, ServerCallContext ctx)
        => await _healthReporter.GetCurrentSnapshotAsync(ctx.CancellationToken);

    public override async Task StreamHealth(
        PingRequest request,
        IServerStreamWriter<HealthStatus> responseStream,
        ServerCallContext ctx)
    {
        var snapshot = await _healthReporter.GetCurrentSnapshotAsync(ctx.CancellationToken);
        await responseStream.WriteAsync(snapshot, ctx.CancellationToken);

        await foreach (var status in _healthReporter.Subscribe(ctx.CancellationToken))
            await responseStream.WriteAsync(status, ctx.CancellationToken);
    }

    public override async Task StreamLogs(
        StreamLogsRequest request,
        IServerStreamWriter<LogEvent> responseStream,
        ServerCallContext ctx)
    {
        await foreach (var entry in _logChannel.Subscribe(ctx.CancellationToken))
        {
            if ((int)entry.Level >= (int)request.MinimumLevel)
                await responseStream.WriteAsync(entry, ctx.CancellationToken);
        }
    }

    public override Task<DeviceInfo> GetDeviceInfo(GetDeviceInfoRequest request, ServerCallContext ctx)
        => Task.FromResult(new DeviceInfo
        {
            Hostname = System.Net.Dns.GetHostName(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLower(),
            OsVersion = System.Runtime.InteropServices.RuntimeEnvironment.GetSystemVersion(),
            DotnetVersion = Environment.Version.ToString(),
            UptimeSeconds = (long)Environment.TickCount64 / 1000,
            MachineId = ReadMachineId(),
        });

    // ── Process Lifecycle ────────────────────────────────────────────────────

    public override async Task<StartApplicationResponse> StartApplication(
        StartApplicationRequest request, ServerCallContext ctx)
    {
        var result = await _processManager.StartApplicationAsync(
            request.AppName, request.UseDebugSlot, ctx.CancellationToken);
        return new StartApplicationResponse
        {
            Success = result.Success,
            Pid = result.Pid,
            ErrorMessage = result.ErrorMessage ?? "",
        };
    }

    public override async Task<StopApplicationResponse> StopApplication(
        StopApplicationRequest request, ServerCallContext ctx)
    {
        var grace = request.GracePeriodSeconds > 0
            ? TimeSpan.FromSeconds(request.GracePeriodSeconds)
            : _opts.ProcessGracefulStopPeriod;
        var result = await _processManager.StopApplicationAsync(
            request.AppName, grace, ctx.CancellationToken);
        return new StopApplicationResponse
        {
            Success = result.Success,
            ExitCode = result.ExitCode,
            ErrorMessage = result.ErrorMessage ?? "",
        };
    }

    public override async Task<RestartApplicationResponse> RestartApplication(
        RestartApplicationRequest request, ServerCallContext ctx)
    {
        var result = await _processManager.RestartApplicationAsync(
            request.AppName, request.UseDebugSlot, ctx.CancellationToken);
        return new RestartApplicationResponse
        {
            Success = result.Success,
            NewPid = result.Pid,
            ErrorMessage = result.ErrorMessage ?? "",
        };
    }

    public override async Task<GetApplicationStatusResponse> GetApplicationStatus(
        GetApplicationStatusRequest request, ServerCallContext ctx)
    {
        var status = await _processManager.GetStatusAsync(
            request.AppName, ctx.CancellationToken);
        return new GetApplicationStatusResponse
        {
            Found = status is not null,
            Status = status ?? new ApplicationStatus(),
        };
    }

    public override async Task<ListProcessesResponse> ListProcesses(
        ListProcessesRequest request, ServerCallContext ctx)
    {
        var statuses = await _processManager.GetAllStatusesAsync(ctx.CancellationToken);
        var response = new ListProcessesResponse();
        response.Apps.AddRange(statuses);
        return response;
    }

    public override async Task StreamOutput(
        StreamOutputRequest request,
        IServerStreamWriter<OutputLine> responseStream,
        ServerCallContext ctx)
    {
        await foreach (var line in _processManager.StreamOutputAsync(
            request.AppName, ctx.CancellationToken))
        {
            await responseStream.WriteAsync(line, ctx.CancellationToken);
        }
    }

    // ── Deployment ───────────────────────────────────────────────────────────

    public override async Task<BeginDeploymentResponse> BeginDeployment(
        BeginDeploymentRequest request, ServerCallContext ctx)
    {
        var result = await _deploymentManager.BeginDeploymentAsync(
            request.AppName, request.Slot, request.Files, ctx.CancellationToken);
        return new BeginDeploymentResponse
        {
            DeploymentId = result.DeploymentId,
            StagingPath = result.StagingPath,
            VersionLabel = result.VersionLabel,
        };
    }

    public override async Task<CommitDeploymentResponse> CommitDeployment(
        CommitDeploymentRequest request, ServerCallContext ctx)
    {
        var result = await _deploymentManager.CommitDeploymentAsync(
            request.AppName, request.Slot, request.Manifest, ctx.CancellationToken);
        return new CommitDeploymentResponse
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage ?? "",
            VersionLabel = result.VersionLabel,
        };
    }

    public override async Task<AbortDeploymentResponse> AbortDeployment(
        AbortDeploymentRequest request, ServerCallContext ctx)
    {
        await _deploymentManager.AbortDeploymentAsync(request.AppName, ctx.CancellationToken);
        return new AbortDeploymentResponse { Success = true };
    }

    public override async Task<ListDeploymentsResponse> ListDeployments(
        ListDeploymentsRequest request, ServerCallContext ctx)
    {
        var records = await _deploymentManager.ListDeploymentsAsync(
            request.AppName, ctx.CancellationToken);
        var response = new ListDeploymentsResponse();
        response.Deployments.AddRange(records);
        return response;
    }

    public override async Task<GetCurrentManifestResponse> GetCurrentManifest(
        GetCurrentManifestRequest request, ServerCallContext ctx)
    {
        var manifest = request.Slot == DeploymentSlot.Debug
            ? await _deploymentManager.GetDebugManifestAsync(request.AppName, ctx.CancellationToken)
            : await _deploymentManager.GetActiveManifestAsync(request.AppName, ctx.CancellationToken);
        return new GetCurrentManifestResponse
        {
            Found = manifest is not null,
            Manifest = manifest ?? new DeploymentManifest(),
        };
    }

    public override async Task<SetActiveVersionResponse> SetActiveVersion(
        SetActiveVersionRequest request, ServerCallContext ctx)
    {
        await _deploymentManager.SetActiveVersionAsync(
            request.AppName, request.VersionLabel, ctx.CancellationToken);
        if (request.AutoRestart)
            await _processManager.RestartApplicationAsync(
                request.AppName, useDebugSlot: false, ctx.CancellationToken);
        return new SetActiveVersionResponse { Success = true };
    }

    public override async Task<DeleteVersionResponse> DeleteVersion(
        DeleteVersionRequest request, ServerCallContext ctx)
    {
        await _deploymentManager.DeleteVersionAsync(
            request.AppName, request.VersionLabel, ctx.CancellationToken);
        return new DeleteVersionResponse { Success = true };
    }

    public override async Task<PruneDeploymentsResponse> PruneDeployments(
        PruneDeploymentsRequest request, ServerCallContext ctx)
    {
        var keep = request.KeepCount > 0
            ? request.KeepCount
            : _opts.DeploymentRetentionCount;
        var deleted = await _deploymentManager.PruneDeploymentsAsync(
            request.AppName, keep, ctx.CancellationToken);
        return new PruneDeploymentsResponse { DeletedCount = deleted };
    }

    // ── vsdbg Management ─────────────────────────────────────────────────────

    public override async Task<GetVsdbgInfoResponse> GetVsdbgInfo(
        GetVsdbgInfoRequest request, ServerCallContext ctx)
    {
        var info = await _vsdbgManager.GetVsdbgInfoAsync(ctx.CancellationToken);
        return new GetVsdbgInfoResponse
        {
            Installed = info is not null,
            Version = info?.Version ?? "",
            InstallPath = info?.InstallPath ?? "",
        };
    }

    public override async Task<InstallVsdbgResponse> InstallVsdbg(
        InstallVsdbgRequest request, ServerCallContext ctx)
    {
        await _vsdbgManager.InstallVsdbgAsync(request.Version, ctx.CancellationToken);
        var info = await _vsdbgManager.GetVsdbgInfoAsync(ctx.CancellationToken);
        return new InstallVsdbgResponse
        {
            Success = true,
            Version = info?.Version ?? request.Version,
        };
    }

    public override async Task<UploadVsdbgTarballResponse> UploadVsdbgTarball(
        UploadVsdbgTarballRequest request, ServerCallContext ctx)
    {
        await _vsdbgManager.InstallVsdbgFromTarballAsync(
            request.TarballPath, request.Version, ctx.CancellationToken);
        return new UploadVsdbgTarballResponse { Success = true };
    }

    // ── Debug Sessions ───────────────────────────────────────────────────────

    public override async Task<StartDebugSessionResponse> StartDebugSession(
        StartDebugSessionRequest request, ServerCallContext ctx)
    {
        var result = await _debugSessionManager.StartDebugSessionAsync(
            request, ctx.CancellationToken);
        return new StartDebugSessionResponse
        {
            Success = result.Success,
            SessionId = result.SessionId,
            VsdbgPid = result.VsdbgPid,
            VsdbgPort = result.VsdbgPort,
            ErrorMessage = result.ErrorMessage ?? "",
        };
    }

    public override async Task<StopDebugSessionResponse> StopDebugSession(
        StopDebugSessionRequest request, ServerCallContext ctx)
    {
        await _debugSessionManager.StopDebugSessionAsync(
            request.SessionId, request.ResumeMeadowDaemon, ctx.CancellationToken);
        return new StopDebugSessionResponse { Success = true };
    }

    public override async Task<GetSessionStatusResponse> GetSessionStatus(
        GetSessionStatusRequest request, ServerCallContext ctx)
    {
        var status = await _debugSessionManager.GetSessionStatusAsync(
            request.SessionId, ctx.CancellationToken);
        return new GetSessionStatusResponse
        {
            Found = status is not null,
            Status = status ?? new SessionStatus(),
        };
    }

    public override async Task<ListSessionsResponse> ListSessions(
        ListSessionsRequest request, ServerCallContext ctx)
    {
        var sessions = await _debugSessionManager.ListSessionsAsync(ctx.CancellationToken);
        var response = new ListSessionsResponse();
        response.Sessions.AddRange(sessions);
        return response;
    }

    // ── Self-Update ──────────────────────────────────────────────────────────

    public override Task<PrepareUpdateResponse> PrepareUpdate(
        PrepareUpdateRequest request, ServerCallContext ctx)
    {
        if (request.NewVersion == CurrentVersion.Version)
            return Task.FromResult(new PrepareUpdateResponse
            {
                Ready = false,
                ErrorMessage = $"Version {request.NewVersion} is already installed",
            });

        var uploadPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)!,
            "meadow-daemon.new");

        return Task.FromResult(new PrepareUpdateResponse
        {
            Ready = true,
            UploadPath = uploadPath,
        });
    }

    public override async Task<ApplyUpdateResponse> ApplyUpdate(
        ApplyUpdateRequest request, ServerCallContext ctx)
    {
        var daemonDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        var newBin = Path.Combine(daemonDir, "meadow-daemon.new");
        var curBin = Path.Combine(daemonDir, "meadow-daemon");

        if (!File.Exists(newBin))
            return new ApplyUpdateResponse
            {
                Success = false,
                ErrorMessage = "meadow-daemon.new not found — call PrepareUpdate first",
            };

        File.SetUnixFileMode(newBin,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        File.Move(newBin, curBin, overwrite: true);

        _log.LogInformation("Self-update applied — exiting for systemd restart");

        _ = Task.Delay(500).ContinueWith(
            _ => _lifetime.StopApplication(),
            TaskScheduler.Default);

        return new ApplyUpdateResponse { Success = true };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ReadMachineId()
    {
        try { return File.ReadAllText("/etc/machine-id").Trim(); }
        catch { return ""; }
    }
}
