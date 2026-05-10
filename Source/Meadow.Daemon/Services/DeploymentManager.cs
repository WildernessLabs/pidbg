namespace Meadow.Daemon.Services;

// Orchestrates begin/commit/abort deployment lifecycle.
// Implemented in Phase 3 (P3.4–P3.9).
internal sealed class DeploymentManager
{
    private readonly VersionStore _versionStore;
    private readonly StagingController _staging;
    private readonly ManifestVerifier _verifier;
    private readonly DaemonOptions _opts;
    private readonly ILogger<DeploymentManager> _log;

    public DeploymentManager(
        VersionStore versionStore,
        StagingController staging,
        ManifestVerifier verifier,
        DaemonOptions opts,
        ILogger<DeploymentManager> log)
    {
        _versionStore = versionStore;
        _staging = staging;
        _verifier = verifier;
        _opts = opts;
        _log = log;
    }
}
