using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.Infrastructure;

public interface IDebugTunnelManager
{
    Task<int> OpenDebugTunnelAsync(SshSession session, int vsdbgPort, CancellationToken ct);
    void CloseDebugTunnel(int localPort);
    void CloseAllTunnels();
}
