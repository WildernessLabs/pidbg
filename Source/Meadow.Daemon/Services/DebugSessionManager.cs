namespace Meadow.Daemon.Services;

// Tracks active debug sessions; handles orphan cleanup after timeout.
// Implemented in Phase 5 (P5.7–P5.8).
internal sealed class DebugSessionManager
{
    private readonly VsdbgLauncher _launcher;
    private readonly ProcessManager _processManager;
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _opts;
    private readonly ILogger<DebugSessionManager> _log;

    public DebugSessionManager(
        VsdbgLauncher launcher,
        ProcessManager processManager,
        StateStore stateStore,
        DaemonOptions opts,
        ILogger<DebugSessionManager> log)
    {
        _launcher = launcher;
        _processManager = processManager;
        _stateStore = stateStore;
        _opts = opts;
        _log = log;
    }
}
