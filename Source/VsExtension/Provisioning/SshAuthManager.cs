using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

// Manages SSH key pair generation (RSA 4096), known_hosts, and authorized_keys.
// Implemented in Phase 6 (P6.7).
internal sealed class SshAuthManager
{
    private readonly SshConnectionManager _ssh;

    public SshAuthManager(SshConnectionManager ssh) => _ssh = ssh;

    public Task<SshKeyPair> GenerateKeyPairAsync(CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");

    public Task InstallPublicKeyAsync(string publicKey, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");

    public Task UpdateKnownHostsAsync(string host, int port, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");
}

internal sealed class SshKeyPair
{
    public required string PrivateKeyPath { get; init; }
    public required string PublicKey { get; init; }
}
