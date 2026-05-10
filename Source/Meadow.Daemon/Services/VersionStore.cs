namespace Meadow.Daemon.Services;

// Manages versioned app directories under appRoot/{app}/versions/{ULID}.
// Implemented in Phase 3 (P3.1).
internal sealed class VersionStore
{
    private readonly DaemonOptions _opts;
    private readonly ILogger<VersionStore> _log;

    public VersionStore(DaemonOptions opts, ILogger<VersionStore> log)
    {
        _opts = opts;
        _log = log;
    }
}
