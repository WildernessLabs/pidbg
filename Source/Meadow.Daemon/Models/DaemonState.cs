namespace Meadow.Daemon.Models;

// Root shape of apps.json
public sealed class AppsState
{
    public List<AppRecord> Apps { get; set; } = new();
}

// Root shape of sessions.json
public sealed class SessionsState
{
    public List<DebugSessionRecord> Sessions { get; set; } = new();
}
