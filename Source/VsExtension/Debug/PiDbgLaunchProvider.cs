using Meadow.Daemon.Contracts.V1;
using PiDbg.Deploy;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

namespace PiDbg.Debug;

// F5 entry point: provisions → deploys → starts app → launches vsdbg →
// opens SSH debug tunnel → hands ICorDebug endpoint to VS.
// Implemented in Phase 5 (P5.9).
internal sealed class PiDbgLaunchProvider
{
    // ICorDebug remote debug engine GUID registered in VS
    internal static readonly Guid DebugEngineGuid =
        new("2E36F1D4-B23C-435D-AB41-18E608940038");

    private readonly ProvisioningOrchestrator _provisioner;
    private readonly DotnetPublishRunner _publisher;
    private readonly SftpDeploymentClient _deployer;
    private readonly MeadowDaemonService.MeadowDaemonServiceClient _grpc;
    private readonly SshDebugTunnel _tunnel;
    private readonly ProjectPropertyReader _projectProps;

    public PiDbgLaunchProvider(
        ProvisioningOrchestrator provisioner,
        DotnetPublishRunner publisher,
        SftpDeploymentClient deployer,
        MeadowDaemonService.MeadowDaemonServiceClient grpc,
        SshDebugTunnel tunnel,
        ProjectPropertyReader projectProps)
    {
        _provisioner = provisioner;
        _publisher = publisher;
        _deployer = deployer;
        _grpc = grpc;
        _tunnel = tunnel;
        _projectProps = projectProps;
    }

    public Task LaunchAsync(string projectPath, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 5");
}
