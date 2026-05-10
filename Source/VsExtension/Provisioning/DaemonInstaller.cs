using PiDbg.Infrastructure;
using Renci.SshNet;

namespace PiDbg.Provisioning;

// Uploads the meadow-daemon binary to the Pi via SFTP and installs it atomically.
// Implemented in Phase 6 (P6.3).
internal sealed class DaemonInstaller
{
    private readonly SshConnectionManager _ssh;

    public DaemonInstaller(SshConnectionManager ssh) => _ssh = ssh;

    public Task InstallAsync(string localBinaryPath, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");

    public Task UpgradeAsync(string localBinaryPath, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");
}
