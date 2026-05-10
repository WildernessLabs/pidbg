namespace Meadow.Daemon.Services;

// Manages app process lifecycle: start, stop, restart, status, output streaming.
// Implemented in Phase 5 (P5.1–P5.3).
internal sealed class ProcessManager
{
    private readonly DeploymentManager _deploymentManager;
    private readonly DaemonOptions _opts;
    private readonly ILogger<ProcessManager> _log;

    public ProcessManager(
        DeploymentManager deploymentManager,
        DaemonOptions opts,
        ILogger<ProcessManager> log)
    {
        _deploymentManager = deploymentManager;
        _opts = opts;
        _log = log;
    }
}
