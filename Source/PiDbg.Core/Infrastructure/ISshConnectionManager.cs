using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.Infrastructure;

public interface ISshConnectionManager
{
    Task<SshSession> ConnectAsync(SshConnectionConfig config, CancellationToken ct);
    void Disconnect(string host);
    SshSession? GetActiveSession(string host);
}
