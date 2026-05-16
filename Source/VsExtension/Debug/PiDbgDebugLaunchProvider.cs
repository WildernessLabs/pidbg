using System.ComponentModel.Composition;

using Meadow.Daemon.Contracts.V1;

using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.VS.Debug;

using PiDbg.Build;
using PiDbg.Deploy;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

namespace PiDbg.Debug;

[Export(typeof(IDebugLaunchProvider))]
[AppliesTo(PiDbgLaunchProfile.CommandName)]
internal sealed class PiDbgDebugLaunchProvider : IDebugLaunchProvider
{
    // VS debug engine GUID for the managed (.NET) debugger — must not change.
    private static readonly Guid ManagedDebugEngineGuid =
        new("2E36F1D4-B23C-435D-AB41-18E608940038");

    private readonly ConfiguredProject _project;

    [ImportingConstructor]
    public PiDbgDebugLaunchProvider(ConfiguredProject project)
    {
        _project = project;
    }

    public async Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        var profile = await PiDbgLaunchProfileWriter
            .TryReadAsync(_project.UnconfiguredProject.FullPath, CancellationToken.None)
            .ConfigureAwait(false);
        return profile.HasValue && !string.IsNullOrEmpty(profile.Value.Config.Host);
    }

    public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsForDebugLaunchAsync(
        DebugLaunchOptions launchOptions)
        => QueryDebugTargetsAsync(launchOptions);

    public async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
        DebugLaunchOptions launchOptions)
    {
        var pkg = PiDbgPackage.Current
            ?? throw new InvalidOperationException("PiDbg package is not yet initialized.");

        var output = PiDbgPackage.OutputWindow;

        var projectFile = _project.UnconfiguredProject.FullPath;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var profile = await PiDbgLaunchProfileWriter.TryReadAsync(projectFile, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Pi profile not found. Use 'Connect to Pi' to configure the connection.");

        var config = profile.Config;
        var appName = profile.AppName;

        if (string.IsNullOrEmpty(config.Host))
            throw new InvalidOperationException(
                "Pi host is not configured. Use 'Connect to Pi' to set up the connection.");

        output.WriteLine(OutputPane.PiDbg, $"=== PiDbg: {appName} on {config.Host} ===");

        // --- Step 1: SSH connection ---
        var session = await PiDbgPackage.Ssh
            .ConnectAsync(config, ct).ConfigureAwait(false);

        // --- Step 2: Provision (idempotent) ---
        output.WriteLine(OutputPane.PiDbg, "Provisioning...");
        var provision = await ProvisioningOrchestrator
            .ProvisionAsync(session, PiDbgPackage.GrpcChannels, output, config.RootFolder, ct)
            .ConfigureAwait(false);

        if (!provision.Success)
            throw new InvalidOperationException(
                $"Provisioning failed: {provision.Error}\n" +
                "See the 'PiDbg Provisioning' output pane for details.");

        var channel = provision.Channel!;

        // --- Step 3: Publish ---
        output.WriteLine(OutputPane.PiDbg, "Publishing...");
        var publisher = new PublishService(output);
        var publishResult = await publisher.PublishAsync(
            projectFile, appName,
            new Progress<string>(s => output.WriteLine(OutputPane.PiDbg, $"  {s}")),
            ct).ConfigureAwait(false);

        output.WriteLine(OutputPane.PiDbg,
            $"Published in {publishResult.Duration.TotalSeconds:F1}s");

        // --- Step 4: Deploy ---
        output.WriteLine(OutputPane.PiDbg, "Deploying...");
        var deployer = new SftpDeploymentClient(session, channel, output);
        await deployer.DeployAsync(
            appName, publishResult.PublishDir, publishResult.Manifest,
            new Progress<DeploymentProgress>(p =>
                output.WriteLine(OutputPane.PiDbg, $"  [{p.Phase}] {p.PercentComplete}%")),
            ct).ConfigureAwait(false);

        // --- Step 5: Start debug session on daemon ---
        output.WriteLine(OutputPane.PiDbg, "Starting debug session...");
        var grpc = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var sessionResp = await grpc.StartDebugSessionAsync(
            new StartDebugSessionRequest
            {
                AppName = appName,
                Mode = SessionMode.Attach,
                CorrelationId = Guid.NewGuid().ToString(),
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!sessionResp.Success)
            throw new InvalidOperationException(
                $"Failed to start debug session: {sessionResp.ErrorMessage}");

        // --- Step 6: Open SSH tunnel for vsdbg port ---
        var localPort = await PiDbgPackage.Tunnels
            .OpenDebugTunnelAsync(session, sessionResp.VsdbgPort, ct)
            .ConfigureAwait(false);

        output.WriteLine(OutputPane.PiDbg,
            $"Tunnel: localhost:{localPort} → {config.Host}:{sessionResp.VsdbgPort}");
        output.WriteLine(OutputPane.PiDbg, "Attaching VS debugger...");

        // --- Step 7: Build VS debug target (attach to already-running vsdbg) ---
        var settings = new DebugLaunchSettings(launchOptions)
        {
            Executable = $"{appName}.dll",
            LaunchDebugEngineGuid = ManagedDebugEngineGuid,
            LaunchOperation = DebugLaunchOperation.AlreadyRunning,
            RemoteMachine = $"127.0.0.1:{localPort}",
            Options = BuildDebugOptions(sessionResp, localPort),
        };

        return new IDebugLaunchSettings[] { settings };
    }

    public async Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        await QueryDebugTargetsAsync(launchOptions).ConfigureAwait(false);
    }

    private static string BuildDebugOptions(StartDebugSessionResponse session, int localPort)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            transport = "tcp",
            port = localPort,
            host = "127.0.0.1",
            sessionId = session.SessionId,
        });
}
