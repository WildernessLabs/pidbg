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
    private readonly ProcessManager _processManager;
    private readonly VsdbgManager _vsdbgManager;
    private readonly DebugSessionManager _debugSessionManager;

    public MeadowDaemonGrpcService(
        IOptions<DaemonOptions> options,
        StateStore stateStore,
        LogEventChannel logChannel,
        ILogger<MeadowDaemonGrpcService> logger,
        IHostApplicationLifetime lifetime,
        IDeploymentManager deploymentManager,
        ProcessManager processManager,
        VsdbgManager vsdbgManager,
        DebugSessionManager debugSessionManager)
    {
        _options = options;
        _stateStore = stateStore;
        _logChannel = logChannel;
        _logger = logger;
        _lifetime = lifetime;
        _deploymentManager = deploymentManager;
        _processManager = processManager;
        _vsdbgManager = vsdbgManager;
        _debugSessionManager = debugSessionManager;
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

    public override Task<StartProcessResponse> StartProcess(StartProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(StartProcess), context);
        throw Unimplemented(nameof(StartProcess));
    }

    public override Task<StopProcessResponse> StopProcess(StopProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(StopProcess), context);
        throw Unimplemented(nameof(StopProcess));
    }

    public override Task<RestartProcessResponse> RestartProcess(RestartProcessRequest request, ServerCallContext context)
    {
        LogCall(nameof(RestartProcess), context);
        throw Unimplemented(nameof(RestartProcess));
    }

    public override Task<GetProcessStatusResponse> GetProcessStatus(GetProcessStatusRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetProcessStatus), context);
        throw Unimplemented(nameof(GetProcessStatus));
    }

    public override Task<ListProcessesResponse> ListProcesses(ListProcessesRequest request, ServerCallContext context)
    {
        LogCall(nameof(ListProcesses), context);
        throw Unimplemented(nameof(ListProcesses));
    }

    public override Task StreamOutput(StreamOutputRequest request, IServerStreamWriter<OutputLine> responseStream, ServerCallContext context)
    {
        LogCall(nameof(StreamOutput), context);
        throw Unimplemented(nameof(StreamOutput));
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
        return new PruneDeploymentsResponse { DeletedCount = 0 }; // We don't track the exact count yet
    }

    // ── vsdbg Management ───────────────────────────────────────────────────────

    public override Task<GetVsdbgInfoResponse> GetVsdbgInfo(GetVsdbgInfoRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetVsdbgInfo), context);
        throw Unimplemented(nameof(GetVsdbgInfo));
    }

    public override Task InstallVsdbg(InstallVsdbgRequest request, IServerStreamWriter<InstallVsdbgProgress> responseStream, ServerCallContext context)
    {
        LogCall(nameof(InstallVsdbg), context);
        throw Unimplemented(nameof(InstallVsdbg));
    }

    public override Task<UploadVsdbgTarballResponse> UploadVsdbgTarball(IAsyncStreamReader<UploadVsdbgTarballRequest> requestStream, ServerCallContext context)
    {
        LogCall(nameof(UploadVsdbgTarball), context);
        throw Unimplemented(nameof(UploadVsdbgTarball));
    }

    // ── Debug Sessions ─────────────────────────────────────────────────────────

    public override Task<StartDebugSessionResponse> StartDebugSession(StartDebugSessionRequest request, ServerCallContext context)
    {
        LogCall(nameof(StartDebugSession), context);
        throw Unimplemented(nameof(StartDebugSession));
    }

    public override Task<StopDebugSessionResponse> StopDebugSession(StopDebugSessionRequest request, ServerCallContext context)
    {
        LogCall(nameof(StopDebugSession), context);
        throw Unimplemented(nameof(StopDebugSession));
    }

    public override Task<GetSessionStatusResponse> GetSessionStatus(GetSessionStatusRequest request, ServerCallContext context)
    {
        LogCall(nameof(GetSessionStatus), context);
        throw Unimplemented(nameof(GetSessionStatus));
    }

    public override Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, ServerCallContext context)
    {
        LogCall(nameof(ListSessions), context);
        throw Unimplemented(nameof(ListSessions));
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
