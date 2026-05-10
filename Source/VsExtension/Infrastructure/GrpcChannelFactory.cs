using Grpc.Net.Client;
using Meadow.Daemon.Contracts.V1;
using Renci.SshNet;

namespace PiDbg.Infrastructure;

// Creates a GrpcChannel routed through an SSH local-port-forward tunnel.
// Implemented in Phase 4 (P4.6).
internal sealed class GrpcChannelFactory : IDisposable
{
    private readonly SshConnectionManager _ssh;
    private ForwardedPortLocal? _tunnel;
    private GrpcChannel? _channel;

    public GrpcChannelFactory(SshConnectionManager ssh) => _ssh = ssh;

    public Task<MeadowDaemonService.MeadowDaemonServiceClient> CreateClientAsync(
        int remoteGrpcPort, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 4");

    public void Dispose()
    {
        _channel?.Dispose();
        _tunnel?.Dispose();
    }
}
