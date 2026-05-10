namespace Meadow.Daemon.Services;

// 5-second PeriodicTimer; detects crash loops (5 restarts in 60s) and
// reconciles /proc/{pid}/cmdline against known-running apps.
// Implemented in Phase 5 (P5.3).
internal sealed class ProcessMonitorService : BackgroundService
{
    private readonly ProcessManager _processManager;
    private readonly ILogger<ProcessMonitorService> _log;

    public ProcessMonitorService(ProcessManager processManager, ILogger<ProcessMonitorService> log)
    {
        _processManager = processManager;
        _log = log;
    }

    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
}
