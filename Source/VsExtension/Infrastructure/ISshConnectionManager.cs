namespace PiDbg.Infrastructure;

public interface ISshConnectionManager
{
    System.Threading.Tasks.Task<SshSession> ConnectAsync(
        SshConnectionConfig config, System.Threading.CancellationToken ct);
    void Disconnect(string host);
    SshSession? GetActiveSession(string host);
}
