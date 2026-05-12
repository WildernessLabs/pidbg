using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Grpc.Core;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Services;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.GrpcService;

internal sealed class MeadowDaemonGrpcService : MeadowDaemonService.MeadowDaemonServiceBase
{
    private readonly IOptions<DaemonOptions> _options;
    private readonly StateStore _stateStore;
    private readonly LogEventChannel _logChannel;
    private readonly ILogger<MeadowDaemonGrpcService> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    private readonly IDeploymentManager _deploymentManager;
    private readonly IProcessManager _processManager;
    private readonly IVsdbgInstaller _vsdbgInstaller;
    private readonly VsdbgManager _vsdbgManager;
    private readonly IDebugSessionManager _sessionManager;

    public MeadowDaemonGrpcService(
        IOptions<DaemonOptions> options,
        StateStore stateStore,
        LogEventChannel logChannel,
        ILogger<MeadowDaemonGrpcService> logger,
        IHostApplicationLifetime lifetime,
        IDeploymentManager deploymentManager,
        IProcessManager processManager,
        IVsdbgInstaller vsdbgInstaller,
        VsdbgManager vsdbgManager,
        IDebugSessionManager sessionManager)
    {
        _options = options;
        _stateStore = stateStore;
        _logChannel = logChannel;
        _logger = logger;
        _lifetime = lifetime;
        _deploymentManager = deploymentManager;
        _processManager = processManager;
        _vsdbgInstaller = vsdbgInstaller;
        _vsdbgManager = vsdbgManager;
        _sessionManager = sessionManager;
    }

    private void LogCall(string rpc, ServerCallContext ctx)
        => _logger.LogDebug("gRPC {Rpc} called by {Peer}", rpc, ctx?.Peer ?? "unknown");

    private static RpcException Unimplemented(string rpc)
        => new RpcException(new Status(StatusCode.Unimplemented, $"{rpc} is not yet implemented"));

    private static void ValidateAppName(string name)
    {
        try { DaemonPaths.SanitizeName(name); }
        catch (ArgumentException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    // ── Diagnostics ────────────────────────────────────────────────────────────

    public override Task<PongResponse> Ping(PingRequest request, ServerCallContext context)
    {
        LogCall(nameof(Ping), context);
        return Task.FromResult(new PongResponse
        {
            Version = BuildDaemonVersion(),
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }

    public override Task<HealthStatus> GetHealth(PingRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetHealth), context);
        throw Unimplemented(nameof(GetHealth));
    }

    public override Task StreamHealth(StreamHealthRequest request, IServerStreamWriter<HealthStatus> responseStream, ServerCallContext context)
    {
        LogCall(nameof(StreamHealth), context);
        throw Unimplemented(nameof(StreamHealth));
    }

    public override async Task StreamLogs(StreamLogsRequest request, IServerStreamWriter<LogEvent> responseStream, ServerCallContext context)
    {
        LogCall(nameof(StreamLogs), context);
        var ct = context.CancellationToken;

        await foreach (var evt in _logChannel.Subscribe(ct))
        {
            if (evt.Level < request.MinimumLevel) continue;

            if (!string.IsNullOrEmpty(request.FilterCategory)
                && !evt.SourceContext.StartsWith(request.FilterCategory, StringComparison.OrdinalIgnoreCase))
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

    public override Task<GetDeviceInfoResponse> GetDeviceInfo(GetDeviceInfoRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetDeviceInfo), context);
        return Task.FromResult(new GetDeviceInfoResponse
        {
            Info = new DeviceInfo
            {
                Hostname = System.Net.Dns.GetHostName(),
                Architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
                OsVersion = RuntimeInformation.OSDescription,
                DotnetVersion = RuntimeInformation.FrameworkDescription,
                UptimeSeconds = Environment.TickCount64 / 1000,
                MachineId = ReadMachineId(),
                DaemonVersion = BuildDaemonVersion()
            }
        });
    }

    // ── Process Lifecycle ──────────────────────────────────────────────────────

    public override async Task<StartProcessResponse> StartProcess(StartProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(StartProcess), context);
        ValidateAppName(request.AppName);
        var result = await _processManager.StartAsync(request.AppName, context.CancellationToken);
        return new StartProcessResponse
        {
            Success = result.Success,
            Pid     = result.Pid ?? 0,
            ErrorMessage = result.Error ?? ""
        };
    }

    public override async Task<StopProcessResponse> StopProcess(StopProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(StopProcess), context);
        ValidateAppName(request.AppName);
        await _processManager.StopAsync(request.AppName, context.CancellationToken);
        return new StopProcessResponse { Success = true };
    }

    public override async Task<RestartProcessResponse> RestartProcess(RestartProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(RestartProcess), context);
        ValidateAppName(request.AppName);
        var result = await _processManager.RestartAsync(request.AppName, context.CancellationToken);
        return new RestartProcessResponse
        {
            Success = result.Success,
            NewPid  = result.Pid ?? 0,
            ErrorMessage = result.Error ?? ""
        };
    }

    public override Task<GetProcessStatusResponse> GetProcessStatus(GetProcessStatusRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetProcessStatus), context);
        ValidateAppName(request.AppName);
        var state = _processManager.GetState(request.AppName);
        var pid   = _processManager.GetPid(request.AppName);
        return Task.FromResult(new GetProcessStatusResponse
        {
            Found = true,
            Status = new ApplicationStatus
            {
                AppName = request.AppName,
                State   = state,
                Pid     = pid ?? 0,
            }
        });
    }

    public override async Task<ListProcessesResponse> ListProcesses(ListProcessesRequest request, ServerCallContext context)
    {
        LogCall(nameof(ListProcesses), context);
        var apps = await _stateStore.LoadAppsAsync(context.CancellationToken);
        var response = new ListProcessesResponse();
        foreach (var app in apps.Apps)
        {
            response.Apps.Add(new ApplicationStatus
            {
                AppName = app.Name,
                State = _processManager.GetState(app.Name),
                Pid = _processManager.GetPid(app.Name) ?? 0
            });
        }
        return response;
    }

    public override async Task StreamOutput(StreamOutputRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        LogCall(nameof(StreamOutput), context);
        ValidateAppName(request.AppName);
        var broadcaster = _processManager.GetOutputBroadcaster(request.AppName);
        var ct = context.CancellationToken;

        await foreach (var line in broadcaster.Subscribe(ct))
        {
            if (request.Stream != OutputStream.Combined && line.Stream != request.Stream)
                continue;

            try { await responseStream.WriteAsync(line, ct); }
            catch (OperationCanceledException) { break; }
            catch { break; }
        }
    }

    // ── Deployment ─────────────────────────────────────────────────────────────

    public override async Task<BeginDeploymentResponse> BeginDeployment(BeginDeploymentRequest request, ServerCallContext context)
    {
        LogCall(nameof(BeginDeployment), context);
        ValidateAppName(request.AppName);

        var result = await _deploymentManager.BeginDeploymentAsync(
            request.AppName,
            request.Manifest,
            request.Slot,
            request.HasDeltaBase ? request.DeltaBase : null,
            context.CancellationToken);

        return new BeginDeploymentResponse
        {
            DeploymentId = result.DeploymentId,
            StagingDir = result.StagingDir,
            FilesNeeded = { result.FilesNeeded }
        };
    }

    public override async Task<CommitDeploymentResponse> CommitDeployment(CommitDeploymentRequest request, ServerCallContext context)
    {
        LogCall(nameof(CommitDeployment), context);
        var result = await _deploymentManager.CommitDeploymentAsync(request.DeploymentId, context.CancellationToken);

        return new CommitDeploymentResponse
        {
            Success = result.Success,
            ErrorMessage = result.ErrorMessage ?? "",
            Failures = { result.Failures?.Select(f => new FileVerificationResult 
            { 
                Path = f.Path, 
                Passed = f.Passed, 
                Error = f.Error ?? "" 
            }) ?? [] }
        };
    }

    public override async Task<AbortDeploymentResponse> AbortDeployment(AbortDeploymentRequest request, ServerCallContext context)
    {
        LogCall(nameof(AbortDeployment), context);
        await _deploymentManager.AbortDeploymentAsync(request.DeploymentId, context.CancellationToken);
        return new AbortDeploymentResponse { Success = true };
    }

    public override async Task<ListDeploymentsResponse> ListDeployments(ListDeploymentsRequest request, ServerCallContext context)
    {
        LogCall(nameof(ListDeployments), context);
        ValidateAppName(request.AppName);

        var versions = await _deploymentManager.ListVersionsAsync(request.AppName);
        var active = await _deploymentManager.GetActiveVersionAsync(request.AppName);

        var response = new ListDeploymentsResponse();
        foreach (var v in versions)
        {
            response.Deployments.Add(new DeploymentRecord
            {
                AppName = request.AppName,
                VersionLabel = v,
                IsActive = (v == active),
                Slot = DeploymentSlot.Production
            });
        }
        return response;
    }

    public override async Task<GetCurrentManifestResponse> GetCurrentManifest(GetCurrentManifestRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetCurrentManifest), context);
        ValidateAppName(request.AppName);

        string? dir = null;
        if (request.Slot == DeploymentSlot.Debug)
        {
            dir = DaemonPaths.AppDebugDir(_options.Value, request.AppName);
        }
        else
        {
            var active = await _deploymentManager.GetActiveVersionAsync(request.AppName);
            if (active != null)
                dir = DaemonPaths.AppVersionDir(_options.Value, request.AppName, active);
        }

        if (dir == null || !Directory.Exists(dir))
            return new GetCurrentManifestResponse { Found = false };

        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
            return new GetCurrentManifestResponse { Found = false };

        try
        {
            await using var fs = File.OpenRead(manifestPath);
            var manifest = await JsonSerializer.DeserializeAsync(fs, DaemonJsonContext.Default.DeploymentManifest, context.CancellationToken);
            return new GetCurrentManifestResponse { Found = manifest != null, Manifest = manifest };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read manifest for {App} from {Path}", request.AppName, manifestPath);
            return new GetCurrentManifestResponse { Found = false };
        }
    }

    public override async Task<SetActiveVersionResponse> SetActiveVersion(SetActiveVersionRequest request, ServerCallContext context)
    {
        LogCall(nameof(SetActiveVersion), context);
        ValidateAppName(request.AppName);

        try
        {
            await _deploymentManager.SetActiveVersionAsync(request.AppName, request.VersionLabel, context.CancellationToken);
            return new SetActiveVersionResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new SetActiveVersionResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<DeleteVersionResponse> DeleteVersion(DeleteVersionRequest request, ServerCallContext context)
    {
        LogCall(nameof(DeleteVersion), context);
        ValidateAppName(request.AppName);

        try
        {
            await _deploymentManager.DeleteVersionAsync(request.AppName, request.VersionLabel);
            return new DeleteVersionResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new DeleteVersionResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<PruneDeploymentsResponse> PruneDeployments(PruneDeploymentsRequest request, ServerCallContext context)
    {
        LogCall(nameof(PruneDeployments), context);
        ValidateAppName(request.AppName);

        var count = request.KeepCount > 0 ? request.KeepCount : _options.Value.DeploymentRetentionCount;
        await _deploymentManager.PruneAsync(request.AppName, count, context.CancellationToken);
        return new PruneDeploymentsResponse { DeletedCount = 0 }; 
    }

    // ── vsdbg Management ───────────────────────────────────────────────────────

    public override async Task<GetVsdbgInfoResponse> GetVsdbgInfo(GetVsdbgInfoRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetVsdbgInfo), context);
        await Task.Yield();
        var version = _vsdbgInstaller.GetInstalledVersion();
        return new GetVsdbgInfoResponse
        {
            Installed = version != null,
            Version = version ?? "",
            InstallPath = DaemonPaths.VsdbgBinPath(_options.Value)
        };
    }

    public override async Task InstallVsdbg(InstallVsdbgRequest request, IServerStreamWriter<InstallVsdbgProgress> responseStream, ServerCallContext context)
    {
        LogCall(nameof(InstallVsdbg), context);
        var progress = new Progress<string>(async msg =>
        {
            try { await responseStream.WriteAsync(new InstallVsdbgProgress { StatusMessage = msg }, context.CancellationToken); }
            catch { /* subscriber gone */ }
        });
        
        try
        {
            await _vsdbgInstaller.InstallAsync(request.Version, progress, context.CancellationToken);
            await responseStream.WriteAsync(new InstallVsdbgProgress { Success = true, StatusMessage = "complete" }, context.CancellationToken);
        }
        catch (Exception ex)
        {
            await responseStream.WriteAsync(new InstallVsdbgProgress { Success = false, ErrorMessage = ex.Message }, context.CancellationToken);
        }
    }

    public override async Task<UploadVsdbgTarballResponse> UploadVsdbgTarball(UploadVsdbgTarballRequest request, ServerCallContext context)
    {
        LogCall(nameof(UploadVsdbgTarball), context);
        if (!File.Exists(request.TarballPath))
            return new UploadVsdbgTarballResponse { Success = false, ErrorMessage = "Tarball not found at specified path" };

        try
        {
            await using var stream = File.OpenRead(request.TarballPath);
            await _vsdbgInstaller.InstallFromTarballAsync(stream, request.Sha256, new Progress<string>(_ => { }), context.CancellationToken);
            return new UploadVsdbgTarballResponse { Success = true };
        }
        catch (Exception ex)
        {
            return new UploadVsdbgTarballResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    // ── Debug Sessions ─────────────────────────────────────────────────────────

    public override async Task<StartDebugSessionResponse> StartDebugSession(StartDebugSessionRequest request, ServerCallContext context)
    {
        LogCall(nameof(StartDebugSession), context);
        ValidateAppName(request.AppName);
        try
        {
            var session = await _sessionManager.StartDebugSessionAsync(
                request.AppName, request.Mode, request.CorrelationId, context.CancellationToken);
            return new StartDebugSessionResponse
            {
                Success = true,
                SessionId = session.SessionId,
                VsdbgPid = session.VsdbgPid,
                VsdbgPort = session.VsdbgPort,
                AppPid    = session.AppPid ?? 0,
            };
        }
        catch (Exception ex)
        {
            return new StartDebugSessionResponse { Success = false, ErrorMessage = ex.Message };
        }
    }

    public override async Task<StopDebugSessionResponse> StopDebugSession(StopDebugSessionRequest request, ServerCallContext context)
    {
        LogCall(nameof(StopDebugSession), context);
        await _sessionManager.StopDebugSessionAsync(request.SessionId, context.CancellationToken);
        return new StopDebugSessionResponse { Success = true };
    }

    public override async Task<GetSessionStatusResponse> GetSessionStatus(GetSessionStatusRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetSessionStatus), context);
        await _sessionManager.TouchSessionAsync(request.SessionId, context.CancellationToken);
        var record = await _sessionManager.GetSessionStatusAsync(request.SessionId, context.CancellationToken);
        if (record is null)
            throw new RpcException(new Status(StatusCode.NotFound, $"Session '{request.SessionId}' not found"));
        
        return new GetSessionStatusResponse 
        { 
            Found = true,
            Status = new SessionStatus
            {
                SessionId = record.SessionId,
                AppName = record.AppName,
                State = record.State,
                VsdbgPort = record.VsdbgPort,
                VsdbgPid = record.VsdbgPid,
                AppPid = record.AppPid ?? 0,
                Mode = record.Mode,
                CorrelationId = record.CorrelationId,
                StartedAtMs = record.StartedAt.ToUnixTimeMilliseconds(),
                LastActivityMs = record.LastActivityAt.ToUnixTimeMilliseconds()
            }
        };
    }

    public override async Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, ServerCallContext context)
    {
        LogCall(nameof(ListSessions), context);
        var sessions = await _sessionManager.ListSessionsAsync(context.CancellationToken);
        var response = new ListSessionsResponse();
        foreach (var s in sessions)
        {
            response.Sessions.Add(new SessionStatus
            {
                SessionId = s.SessionId,
                AppName = s.AppName,
                State = s.State,
                VsdbgPort = s.VsdbgPort,
                VsdbgPid = s.VsdbgPid,
                AppPid = s.AppPid ?? 0,
                Mode = s.Mode,
                CorrelationId = s.CorrelationId,
                StartedAtMs = s.StartedAt.ToUnixTimeMilliseconds(),
                LastActivityMs = s.LastActivityAt.ToUnixTimeMilliseconds()
            });
        }
        return response;
    }

    // ── Self-Update ────────────────────────────────────────────────────────────

    public override Task<PrepareUpdateResponse> PrepareUpdate(IAsyncStreamReader<PrepareUpdateChunk> requestStream, ServerCallContext context)
    {
        LogCall(nameof(PrepareUpdate), context);
        throw Unimplemented(nameof(PrepareUpdate));
    }

    public override Task<ApplyUpdateResponse> ApplyUpdate(ApplyUpdateRequest request, ServerCallContext context)
    {
        LogCall(nameof(ApplyUpdate), context);
        throw Unimplemented(nameof(ApplyUpdate));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string ReadMachineId()
    {
        try 
        { 
            var id = File.ReadAllText("/etc/machine-id").Trim();
            return id.Length > 32 ? id[..32] : id;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    private static DaemonVersion BuildDaemonVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        return new DaemonVersion
        {
            Version = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0",
            ProtocolVersion = 1,
            GitCommit = "" // populated in later phases
        };
    }
}
