using System.ComponentModel.DataAnnotations;

namespace Meadow.Daemon.Services;

public sealed class DaemonOptions
{
    public const string Section = "Daemon";

    // Network
    [Range(1, 65535)]
    public int GrpcPort { get; init; } = 50051;

    [Range(1, 65535)]
    public int RestPort { get; init; } = 5000;

    // Filesystem roots (must be absolute paths)
    [Required]
    public string InstallRoot { get; init; } = "/opt/meadow";

    [Required]
    public string AppRoot { get; init; } = "/opt/meadow/apps";

    [Required]
    public string VsdbgRoot { get; init; } = "/opt/meadow/vsdbg";

    [Required]
    public string StateRoot { get; init; } = "/opt/meadow/state";

    [Required]
    public string LogRoot { get; init; } = "/opt/meadow/logs";

    // Deployment
    [Range(1, 20)]
    public int DeploymentRetentionCount { get; init; } = 3;

    // vsdbg port range
    [Range(1024, 65535)]
    public int VsdbgPortRangeStart { get; init; } = 4024;

    [Range(1024, 65535)]
    public int VsdbgPortRangeEnd { get; init; } = 4124;

    // Process lifecycle
    [Range(1, 60)]
    public int ProcessGracefulStopSeconds { get; init; } = 5;

    public bool AutoRestartManagedApp { get; init; } = true;

    [Range(1, 1440)]
    public int DebugSessionOrphanTimeoutMinutes { get; init; } = 30;

    // Computed TimeSpan helpers (not bound from config)
    public TimeSpan ProcessGracefulStopTimeout
        => TimeSpan.FromSeconds(ProcessGracefulStopSeconds);

    public TimeSpan DebugSessionOrphanTimeout
        => TimeSpan.FromMinutes(DebugSessionOrphanTimeoutMinutes);

    // Validation beyond DataAnnotations
    public IEnumerable<string> GetValidationErrors()
    {
        if (VsdbgPortRangeEnd <= VsdbgPortRangeStart)
            yield return "VsdbgPortRangeEnd must be greater than VsdbgPortRangeStart";

        foreach (var root in new[] { InstallRoot, AppRoot, VsdbgRoot, StateRoot, LogRoot })
        {
            if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root))
                yield return $"Path '{root}' must be an absolute path";
        }
    }
}
