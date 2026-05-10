namespace Meadow.Daemon.Services;

// MQTT-based OTA update listener.
// Implemented in Phase 7 (P7.7).
internal sealed class OtaUpdateService : BackgroundService
{
    private readonly ILogger<OtaUpdateService> _log;

    public OtaUpdateService(ILogger<OtaUpdateService> log) => _log = log;

    protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
}
