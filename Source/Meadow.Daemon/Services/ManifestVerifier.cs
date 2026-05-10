namespace Meadow.Daemon.Services;

// Verifies deployed file manifests via parallel SHA-256 comparison.
// Implemented in Phase 3 (P3.3).
internal sealed class ManifestVerifier
{
    private readonly ILogger<ManifestVerifier> _log;

    public ManifestVerifier(ILogger<ManifestVerifier> log) => _log = log;
}
