namespace Meadow.Daemon.Services;

// Controls the staging area and hard-link delta transfer.
// Implemented in Phase 3 (P3.2).
internal sealed class StagingController
{
    private readonly DaemonOptions _opts;
    private readonly ILogger<StagingController> _log;

    public StagingController(DaemonOptions opts, ILogger<StagingController> log)
    {
        _opts = opts;
        _log = log;
    }
}
