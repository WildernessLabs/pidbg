using Renci.SshNet;

namespace PiDbg.Infrastructure;

// Manages a persistent SSH connection to the target Pi.
// Reconnects automatically on transient failures.
// Implemented in Phase 4 (P4.3).
internal sealed class SshConnectionManager : IDisposable
{
    private SshClient? _client;
    private readonly object _lock = new();

    public string Host { get; private set; } = "";
    public int Port { get; private set; } = 22;
    public string Username { get; private set; } = "";
    public bool IsConnected => _client?.IsConnected == true;

    public Task ConnectAsync(string host, int port, string username,
        string privateKeyPath, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 4");

    public Task DisconnectAsync()
        => throw new NotImplementedException("Implemented in Phase 4");

    public SshClient GetClient()
    {
        if (_client is null || !_client.IsConnected)
            throw new InvalidOperationException("Not connected");
        return _client;
    }

    public Task<string> ExecuteCommandAsync(string command, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 4");

    public void Dispose() => _client?.Dispose();
}
