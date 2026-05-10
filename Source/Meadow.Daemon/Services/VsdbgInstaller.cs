namespace Meadow.Daemon.Services;

// Downloads and installs vsdbg via GetVsDbg.sh or tarball upload.
// Implemented in Phase 5 (P5.5).
internal sealed class VsdbgInstaller
{
    private readonly DaemonOptions _opts;
    private readonly ILogger<VsdbgInstaller> _log;

    public VsdbgInstaller(DaemonOptions opts, ILogger<VsdbgInstaller> log)
    {
        _opts = opts;
        _log = log;
    }
}
