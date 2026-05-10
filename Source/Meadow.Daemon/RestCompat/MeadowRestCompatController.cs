using Microsoft.AspNetCore.Mvc;
using Meadow.Daemon.Services;

namespace Meadow.Daemon.RestCompat;

// REST compatibility shim for tooling that predates the gRPC API.
// Implemented in Phase 2 (P2.8).
[ApiController]
[Route("api")]
internal sealed class MeadowRestCompatController : ControllerBase
{
    private readonly ProcessManager _processManager;
    private readonly ILogger<MeadowRestCompatController> _log;

    public MeadowRestCompatController(ProcessManager processManager, ILogger<MeadowRestCompatController> log)
    {
        _processManager = processManager;
        _log = log;
    }

    [HttpGet("health")]
    public IActionResult GetHealth() => Ok(new { status = "ok" });

    [HttpPost("app/{appName}/start")]
    public Task<IActionResult> StartApp(string appName) =>
        throw new NotImplementedException("Implemented in Phase 2");

    [HttpPost("app/{appName}/stop")]
    public Task<IActionResult> StopApp(string appName) =>
        throw new NotImplementedException("Implemented in Phase 2");
}
