namespace Meadow.Daemon.Services;

// 30-second PeriodicTimer; publishes device health snapshots.
// Implemented in Phase 7 (P7.6).
internal sealed class HealthReporterService : BackgroundService
{
    private readonly ILogger<HealthReporterService> _log;

    public HealthReporterService(ILogger<HealthReporterService> log) => _log = log;

    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
}
