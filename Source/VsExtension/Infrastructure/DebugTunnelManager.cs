using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace PiDbg.Infrastructure;

internal sealed class DebugTunnelManager : IDebugTunnelManager, IDisposable
{
    private readonly ConcurrentDictionary<int, ForwardedPortLocal> _tunnels =
        new ConcurrentDictionary<int, ForwardedPortLocal>();

    public async Task<int> OpenDebugTunnelAsync(
        SshSession session, int vsdbgPort, CancellationToken ct)
    {
        var (fwd, localPort) = await session.OpenTunnelAsync(vsdbgPort, ct)
                                            .ConfigureAwait(false);

        // Keep-alive prevents SSH idle-timeout from dropping long debug sessions
        session.Ssh.KeepAliveInterval = TimeSpan.FromSeconds(30);

        _tunnels[localPort] = fwd;
        return localPort;
    }

    public void CloseDebugTunnel(int localPort)
    {
        if (_tunnels.TryRemove(localPort, out var fwd))
            fwd.Stop();
    }

    public void CloseAllTunnels()
    {
        foreach (var fwd in _tunnels.Values)
        {
            try { fwd.Stop(); } catch { /* ignore — session may already be gone */ }
        }
        _tunnels.Clear();
    }

    public void Dispose() => CloseAllTunnels();
}
