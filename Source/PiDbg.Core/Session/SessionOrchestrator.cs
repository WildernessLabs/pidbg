using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Meadow.Daemon.Contracts.V1;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using PiDbg.Build;
using PiDbg.Deploy;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

namespace PiDbg.Core;

public sealed partial class SessionOrchestrator
{
    private readonly ISshConnectionManager _ssh;
    private readonly IGrpcChannelFactory   _grpcChannels;
    private readonly IDebugTunnelManager   _tunnels;
    private readonly IPublishRunner        _publisher;
    private readonly IProvisioningService  _provisioning;
    private readonly ILogger<SessionOrchestrator> _logger;

    public SessionOrchestrator(
        ISshConnectionManager     ssh,
        IGrpcChannelFactory       grpcChannels,
        IDebugTunnelManager       tunnels,
        IPublishRunner            publisher,
        IProvisioningService      provisioning,
        ILogger<SessionOrchestrator> logger)
    {
        _ssh          = ssh;
        _grpcChannels = grpcChannels;
        _tunnels      = tunnels;
        _publisher    = publisher;
        _provisioning = provisioning;
        _logger       = logger;
    }

    public async Task<DebugSessionInfo> RunAsync(
        SessionRequest    request,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version;
        var versionText = version is null ? "" : $" (v{version.Major}.{version.Minor}.{version.Build})";
        progress.Report($"=== PiDbg{versionText}: {request.AppName} on {request.Connection.Host} ===");

        // --- Step 1: SSH ---
        progress.Report("Connecting...");
        var session = await _ssh.ConnectAsync(request.Connection, ct).ConfigureAwait(false);

        // --- Step 1.5: Verify device has a compatible .NET runtime for this app ---
        // Read directly from the project's <TargetFramework> - known immediately, no
        // publish needed - so a mismatch is caught and reported (or fixed) up front,
        // before wasting time on provisioning/publish for a session that can't work.
        var requiredFx = RuntimeInstaller.ReadRequiredFrameworkFromProject(request.ProjectPath);
        if (requiredFx is { } required)
        {
            progress.Report("Checking .NET runtime...");
            var (satisfied, installedText) = await RuntimeInstaller
                .IsSatisfiedAsync(session, required, ct).ConfigureAwait(false);

            if (!satisfied)
            {
                if (!request.DeployRuntimeIfNecessary)
                    throw new InvalidOperationException(
                        $"Project targets .NET {required.Name} {required.Version}, but device only has " +
                        $"{installedText} installed. Set \"deployRuntimeIfNecessary\": true in launch.json " +
                        $"to install it automatically, or install .NET {required.Version} manually on the device.");

                progress.Report($"Installing .NET {required.Version}...");
                await RuntimeInstaller.InstallAsync(session, required, progress, ct).ConfigureAwait(false);
            }
        }

        // --- Step 2: Provision ---
        progress.Report("Provisioning...");
        var provision = await _provisioning
            .ProvisionAsync(session, request.Connection.RootFolder, progress, ct)
            .ConfigureAwait(false);

        if (!provision.Success)
            throw new InvalidOperationException($"Provisioning failed: {provision.Error}");

        var channel = provision.GrpcChannel!;

        // --- Step 3: Publish ---
        progress.Report("Publishing...");
        var publishResult = await _publisher
            .PublishAsync(request.ProjectPath, request.AppName, progress, ct)
            .ConfigureAwait(false);
        progress.Report($"Published in {publishResult.Duration.TotalSeconds:F1}s");

        // --- Step 4: Deploy ---
        progress.Report("Deploying...");
        var deployer = new SftpDeploymentClient(session, channel, _logger);
        var deployProgress = new Progress<DeploymentProgress>(p =>
            progress.Report($"  [{p.Phase}] {p.PercentComplete}%"));
        await deployer.DeployAsync(
            request.AppName, publishResult.PublishDir, publishResult.Manifest,
            deployProgress, ct).ConfigureAwait(false);

        // --- Step 5: Start debug session on daemon ---
        progress.Report("Starting debug session...");
        var grpc = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var sessionResp = await grpc.StartDebugSessionAsync(
            new StartDebugSessionRequest
            {
                AppName       = request.AppName,
                Mode          = SessionMode.Attach,
                CorrelationId = Guid.NewGuid().ToString(),
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!sessionResp.Success)
            throw new InvalidOperationException(
                $"Failed to start debug session: {sessionResp.ErrorMessage}");

        // --- Step 6: SSH tunnel for vsdbg ---
        var localPort = await _tunnels
            .OpenDebugTunnelAsync(session, sessionResp.VsdbgPort, ct)
            .ConfigureAwait(false);

        progress.Report($"Tunnel: localhost:{localPort} → {request.Connection.Host}:{sessionResp.VsdbgPort}");

        var absRoot    = session.ExpandPath(request.Connection.RootFolder);
        var appDllPath = $"{absRoot}/apps/{request.AppName}/debug/{request.AppName}.dll";

        LogSessionReady(_logger, localPort, sessionResp.AppPid);

        return new DebugSessionInfo
        {
            LocalPort  = localPort,
            AppPid     = sessionResp.AppPid,
            AppDllPath = appDllPath,
        };
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Debug session ready: local port {LocalPort}, PID {Pid}")]
    private static partial void LogSessionReady(ILogger logger, int localPort, int pid);
}
