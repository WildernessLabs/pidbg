using PiDbg.Infrastructure;
using Renci.SshNet;

namespace PiDbg.Debug;

// Opens an SSH local-port-forward tunnel for the vsdbg ICorDebug port.
// Implemented in Phase 5 (P5.10).
internal sealed class SshDebugTunnel : IDisposable
{
    private readonly SshConnectionManager _ssh;
    private ForwardedPortLocal? _port;

    public SshDebugTunnel(SshConnectionManager ssh) => _ssh = ssh;

    public int LocalPort { get; private set; }

    public Task<int> OpenAsync(int remoteVsdbgPort, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 5");

    public void Close()
    {
        _port?.Stop();
        _port?.Dispose();
        _port = null;
    }

    public void Dispose() => Close();
}
