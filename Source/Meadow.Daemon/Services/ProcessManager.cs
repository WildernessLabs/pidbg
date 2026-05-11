using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

// Manages app process lifecycle: start, stop, restart, status, output streaming.
// Implemented in Phase 5 (P5.1–P5.3).
internal class ProcessManager
{
    private readonly IDeploymentManager _deploymentManager;
    private readonly DaemonOptions _opts;
    private readonly ILogger<ProcessManager> _log;

    public ProcessManager(
        IDeploymentManager deploymentManager,
        IOptions<DaemonOptions> opts,
        ILogger<ProcessManager> log)
    {
        _deploymentManager = deploymentManager;
        _opts = opts.Value;
        _log = log;
    }
}
