using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

// Downloads and installs vsdbg via GetVsDbg.sh or tarball upload.
// Implemented in Phase 5 (P5.5).
internal class VsdbgInstaller
{
    private readonly DaemonOptions _opts;
    private readonly ILogger<VsdbgInstaller> _log;

    public VsdbgInstaller(IOptions<DaemonOptions> opts, ILogger<VsdbgInstaller> log)
    {
        _opts = opts.Value;
        _log = log;
    }
}
