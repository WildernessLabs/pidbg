using System.ComponentModel.Composition;
using System.IO;

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
[AppliesTo(PiDbgCapability)]
internal sealed class PiDbgDebugLaunchProvider : IDebugLaunchProvider
{
    public const string PiDbgCapability = "PiDbg";

    // VS debug engine GUID for the managed (.NET) debugger — must not change.
    private static readonly Guid ManagedDebugEngineGuid =
        new Guid("2E36F1D4-B23C-435D-AB41-18E608940038");

    private readonly ConfiguredProject _project;

    [ImportingConstructor]
    public PiDbgDebugLaunchProvider(ConfiguredProject project)
    {
        _project = project;
    }

    public async Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        var host = await GetPropertyAsync(ProjectPropertyReader.PropHost).ConfigureAwait(false);
        return !string.IsNullOrEmpty(host);
    }

    // Called when launching with the debugger attached (F5).
    public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsForDebugLaunchAsync(
        DebugLaunchOptions launchOptions)
        => QueryDebugTargetsAsync(launchOptions);

    public async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
        DebugLaunchOptions launchOptions)
    {
        var pkg = PiDbgPackage.Current
            ?? throw new InvalidOperationException("PiDbg package is not yet initialized.");

        var output = PiDbgPackage.OutputWindow;
        output.Activate(OutputPane.PiDbg);

        // --- Read project properties ---
        var host = await GetPropertyAsync(ProjectPropertyReader.PropHost).ConfigureAwait(false);
        if (string.IsNullOrEmpty(host))
            throw new InvalidOperationException(
                "PiDbgHost project property is not set. " +
                "Configure it via Project Properties → PiDbg tab.");

        var portStr = await GetPropertyAsync(ProjectPropertyReader.PropPort).ConfigureAwait(false);
        var port = int.TryParse(portStr, out var p) && p > 0 ? p : 22;
        var user = await GetPropertyAsync(ProjectPropertyReader.PropUsername).ConfigureAwait(false);
        var keyFile = await GetPropertyAsync(ProjectPropertyReader.PropPrivateKeyPath).ConfigureAwait(false);
        var appNameProp = await GetPropertyAsync(ProjectPropertyReader.PropAppName).ConfigureAwait(false);

        var projectFile = _project.UnconfiguredProject.FullPath;
        var appName = string.IsNullOrEmpty(appNameProp)
            ? Path.GetFileNameWithoutExtension(projectFile)
            : appNameProp;

        var config = new SshConnectionConfig
        {
            Host = host,
            Port = port,
            User = string.IsNullOrEmpty(user) ? "pi" : user,
            KeyFile = string.IsNullOrEmpty(keyFile) ? null : keyFile,
        };

        output.WriteLine(OutputPane.PiDbg, $"=== PiDbg: {appName} on {host} ===");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // --- Step 1: SSH connection ---
        var session = await PiDbgPackage.Ssh
            .ConnectAsync(config, ct).ConfigureAwait(false);

        // --- Step 2: Provision (idempotent) ---
        output.WriteLine(OutputPane.PiDbg, "Provisioning...");
        var provision = await ProvisioningOrchestrator
            .ProvisionAsync(session, PiDbgPackage.GrpcChannels, output, ct)
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
            $"Tunnel: localhost:{localPort} → {host}:{sessionResp.VsdbgPort}");
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

    private async Task<string> GetPropertyAsync(string name)
    {
        var props = _project.Services.ProjectPropertiesProvider?.GetCommonProperties();
        if (props == null) return "";
        return await props.GetEvaluatedPropertyValueAsync(name).ConfigureAwait(false);
    }

    private static string BuildDebugOptions(StartDebugSessionResponse session, int localPort)
        => System.Text.Json.JsonSerializer.Serialize(new
        {
            transport = "tcp",
            port = localPort,
            host = "127.0.0.1",
            sessionId = session.SessionId,
        });

    // Called when launching without the debugger (Ctrl+F5).
    public async Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        var targets = await QueryDebugTargetsAsync(launchOptions).ConfigureAwait(false);
        foreach (var target in targets)
        {
            // For now, we don't have a separate "run without debug" path on the daemon
            // so we just launch it and let VS handle what it can.
        }
    }
}
