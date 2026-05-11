using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Meadow.Daemon.GrpcService;

public sealed class DaemonHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // Phase 2: always healthy once started.
        // Phase 7: add real checks (state store readable, vsdbg dir exists, etc.)
        return Task.FromResult(HealthCheckResult.Healthy("Meadow Daemon ready"));
    }
}
