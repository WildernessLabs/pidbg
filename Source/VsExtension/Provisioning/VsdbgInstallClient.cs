using Meadow.Daemon.Contracts.V1;

namespace PiDbg.Provisioning;

// Installs vsdbg on the Pi: first tries GetVsDbg.sh via gRPC, falls back
// to tarball upload if the Pi has no internet access.
// Implemented in Phase 6 (P6.4).
internal sealed class VsdbgInstallClient
{
    private readonly MeadowDaemonService.MeadowDaemonServiceClient _grpc;

    public VsdbgInstallClient(MeadowDaemonService.MeadowDaemonServiceClient grpc) => _grpc = grpc;

    public Task InstallAsync(string version, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");

    public Task InstallFromTarballAsync(string localTarballPath, string version, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");
}
