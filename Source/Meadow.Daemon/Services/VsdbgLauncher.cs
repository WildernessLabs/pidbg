using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

// Launches vsdbg in --server mode and polls /proc/net/tcp6 for LISTEN state.
// Implemented in Phase 5 (P5.6).
internal class VsdbgLauncher
{
    private readonly VsdbgManager _vsdbgManager;
    private readonly DaemonOptions _opts;
    private readonly ILogger<VsdbgLauncher> _log;

    public VsdbgLauncher(
        VsdbgManager vsdbgManager,
        IOptions<DaemonOptions> opts,
        ILogger<VsdbgLauncher> log)
    {
        _vsdbgManager = vsdbgManager;
        _opts = opts.Value;
        _log = log;
    }
}
