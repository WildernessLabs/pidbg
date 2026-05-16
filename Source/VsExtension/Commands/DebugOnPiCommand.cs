using System.ComponentModel.Design;

using Meadow.Daemon.Contracts.V1;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using PiDbg.Build;
using PiDbg.Debug;
using PiDbg.Deploy;
using PiDbg.Infrastructure;
using PiDbg.ProjectSystem;
using PiDbg.Provisioning;

namespace PiDbg.Commands;

internal sealed class DebugOnPiCommand
{
    // Managed (.NET) debug engine GUID — must match what VS uses for CoreCLR attach.
    private static readonly Guid ManagedDebugEngineGuid =
        new("2E36F1D4-B23C-435D-AB41-18E608940038");

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as IMenuCommandService
            ?? throw new InvalidOperationException("IMenuCommandService unavailable");

        var cmdId = new CommandID(PackageGuids.CommandSet, CommandIds.DebugOnPi);
        commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
    }

    private static void Execute(object sender, EventArgs e)
    {
        var pkg = PiDbgPackage.Current!;
        PiDbgPackage.OutputWindow.Activate(OutputPane.PiDbg);
        pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                await RunAsync(pkg).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(
                    pkg,
                    $"Pi Debugger failed to launch:\n\n{ex.Message}",
                    "Pi Debugger",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });
    }

    private static async Task RunAsync(AsyncPackage pkg)
    {
        var output = PiDbgPackage.OutputWindow;
        var ensurer = new PiDbgCapabilityEnsurer(pkg);
        var projectPath = await ensurer.GetProjectPathAsync(CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No startup project found. Set a startup project in Solution Explorer and try again.");

        var profile = await PiDbgLaunchProfileWriter
            .TryReadAsync(projectPath, CancellationToken.None)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "No Pi connection configured. Use 'Pi Debugger → Connect to Pi...' first.");

        var config = profile.Config;
        var appName = profile.AppName;

        if (string.IsNullOrEmpty(config.Host))
            throw new InvalidOperationException(
                "Pi host is not configured. Use 'Pi Debugger → Connect to Pi...' first.");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        output.WriteLine(OutputPane.PiDbg, $"=== PiDbg: {appName} on {config.Host} ===");

        // Step 1: SSH connection
        var session = await PiDbgPackage.Ssh
            .ConnectAsync(config, ct).ConfigureAwait(false);

        // Step 2: Provision (idempotent)
        output.WriteLine(OutputPane.PiDbg, "Provisioning...");
        var provision = await ProvisioningOrchestrator
            .ProvisionAsync(session, PiDbgPackage.GrpcChannels, output, config.RootFolder, ct)
            .ConfigureAwait(false);

        if (!provision.Success)
            throw new InvalidOperationException(
                $"Provisioning failed: {provision.Error}");

        var channel = provision.Channel!;

        // Step 3: Publish
        output.WriteLine(OutputPane.PiDbg, "Publishing...");
        var publisher = new PublishService(output);
        var publishResult = await publisher.PublishAsync(
            projectPath, appName,
            new Progress<string>(s => output.WriteLine(OutputPane.PiDbg, $"  {s}")),
            ct).ConfigureAwait(false);

        output.WriteLine(OutputPane.PiDbg,
            $"Published in {publishResult.Duration.TotalSeconds:F1}s");

        // Step 4: Deploy
        output.WriteLine(OutputPane.PiDbg, "Deploying...");
        var deployer = new SftpDeploymentClient(session, channel, output);
        await deployer.DeployAsync(
            appName, publishResult.PublishDir, publishResult.Manifest,
            new Progress<DeploymentProgress>(p =>
                output.WriteLine(OutputPane.PiDbg, $"  [{p.Phase}] {p.PercentComplete}%")),
            ct).ConfigureAwait(false);

        // Step 5: Start debug session on daemon
        output.WriteLine(OutputPane.PiDbg, "Starting debug session...");
        var grpc = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var sessionResp = await grpc.StartDebugSessionAsync(
            new StartDebugSessionRequest
            {
                AppName   = appName,
                Mode      = SessionMode.Attach,
                CorrelationId = Guid.NewGuid().ToString(),
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!sessionResp.Success)
            throw new InvalidOperationException(
                $"Failed to start debug session: {sessionResp.ErrorMessage}");

        // Step 6: Open SSH tunnel for vsdbg port
        var localPort = await PiDbgPackage.Tunnels
            .OpenDebugTunnelAsync(session, sessionResp.VsdbgPort, ct)
            .ConfigureAwait(false);

        output.WriteLine(OutputPane.PiDbg,
            $"Tunnel: localhost:{localPort} → {config.Host}:{sessionResp.VsdbgPort}");
        output.WriteLine(OutputPane.PiDbg, "Attaching VS debugger...");

        // Step 7: Attach — must run on UI thread
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        var debugger = await pkg.GetServiceAsync(typeof(SVsShellDebugger)) as IVsDebugger4
            ?? throw new InvalidOperationException("IVsDebugger4 unavailable.");

        var targetInfo = new VsDebugTargetInfo4
        {
            dlo = 2u, // DLO_AlreadyRunning — attach to running process
            bstrExe = $"{appName}.dll",
            guidLaunchDebugEngine = ManagedDebugEngineGuid,
            dwDebugEngineCount = 0,
            pDebugEngines = IntPtr.Zero,
            bstrRemoteMachine = $"127.0.0.1:{localPort}",
            bstrOptions = System.Text.Json.JsonSerializer.Serialize(new
            {
                transport  = "tcp",
                port       = localPort,
                host       = "127.0.0.1",
                sessionId  = sessionResp.SessionId,
            }),
        };

        var processInfo = new VsDebugTargetProcessInfo[1];
        debugger.LaunchDebugTargets4(1, new[] { targetInfo }, processInfo);

        output.WriteLine(OutputPane.PiDbg, "Debugger attached.");
    }
}
