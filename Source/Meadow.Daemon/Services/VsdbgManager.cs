namespace Meadow.Daemon.Services;

// Coordinates vsdbg install state queries and tarball-based installs.
// Implemented in Phase 5 (P5.5).
internal sealed class VsdbgManager
{
    private readonly VsdbgInstaller _installer;
    private readonly DaemonOptions _opts;
    private readonly ILogger<VsdbgManager> _log;

    public VsdbgManager(
        VsdbgInstaller installer,
        DaemonOptions opts,
        ILogger<VsdbgManager> log)
    {
        _installer = installer;
        _opts = opts;
        _log = log;
    }
}
