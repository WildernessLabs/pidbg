namespace Meadow.Daemon.Services;

public sealed class DaemonOptions
{
    public int    GrpcPort                        { get; init; } = 50051;
    public int    RestPort                        { get; init; } = 5000;
    public string AppRoot                         { get; init; } = "/opt/meadow/apps";
    public string VsdbgRoot                       { get; init; } = "/opt/meadow/vsdbg";
    public string StateRoot                       { get; init; } = "/opt/meadow/state";
    public string LogRoot                         { get; init; } = "/opt/meadow/logs";
    public int    DeploymentRetentionCount        { get; init; } = 3;
    public int    VsdbgPortRangeStart             { get; init; } = 4024;
    public int    VsdbgPortRangeEnd               { get; init; } = 4124;
    public int    ProcessGracefulStopSeconds      { get; init; } = 5;
    public bool   AutoRestartManagedApp           { get; init; } = true;
    public int    DebugSessionOrphanTimeoutMinutes { get; init; } = 30;

    public TimeSpan ProcessGracefulStopPeriod =>
        TimeSpan.FromSeconds(ProcessGracefulStopSeconds);

    public TimeSpan DebugSessionOrphanTimeout =>
        TimeSpan.FromMinutes(DebugSessionOrphanTimeoutMinutes);
}
