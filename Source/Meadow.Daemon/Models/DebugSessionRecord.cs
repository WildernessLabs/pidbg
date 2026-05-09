using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Models;

public sealed record DebugSessionRecord
{
    public string       SessionId      { get; init; } = "";
    public string       AppName        { get; init; } = "";
    public int          VsdbgPid       { get; init; }
    public int          VsdbgPort      { get; init; }
    public int?         AppPid         { get; init; }
    public SessionMode  Mode           { get; init; }
    public SessionState State          { get; set; } = SessionState.Starting;
    public DateTimeOffset StartedAt    { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt { get; set; } = DateTimeOffset.UtcNow;
    public string       CorrelationId  { get; init; } = "";
}
