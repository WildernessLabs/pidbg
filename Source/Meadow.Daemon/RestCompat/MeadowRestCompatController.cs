using System.Reflection;
using Microsoft.AspNetCore.Mvc;

namespace Meadow.Daemon.RestCompat;

[ApiController]
[Route("api/v1")]
public sealed class MeadowRestCompatController : ControllerBase
{
    private readonly ILogger<MeadowRestCompatController> _logger;

    public MeadowRestCompatController(ILogger<MeadowRestCompatController> logger)
        => _logger = logger;

    // Health probe — used by scripts and existing tooling
    [HttpGet("health")]
    public IActionResult GetHealth()
        => Ok(new HealthResponse { Status = "ok", Version = GetVersion() });

    public sealed class HealthResponse
    {
        public string Status { get; set; } = "";
        public string Version { get; set; } = "";
    }

    // App list stub — returns empty array, original API shape preserved
    [HttpGet("apps")]
    public IActionResult ListApps()
        => Ok(Array.Empty<object>());

    // All write operations redirect to gRPC
    [HttpPost("apps")]
    [HttpDelete("apps/{name}")]
    [HttpPost("apps/{name}/start")]
    [HttpPost("apps/{name}/stop")]
    public IActionResult GrpcOnly()
        => StatusCode(501, new {
            error = "Use gRPC API (port 50051). REST write operations are not supported.",
            grpcService = "meadow.daemon.v1.MeadowDaemonService"
        });

    private static string GetVersion()
        => Assembly.GetExecutingAssembly()
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion ?? "0.0.0";
}
