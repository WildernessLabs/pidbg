namespace Meadow.Daemon.Models;

public sealed record AppRecord
{
    public string Name                                         { get; init; } = "";
    public string EntryPoint                                   { get; init; } = "";
    public string? StartupArgs                                 { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = new Dictionary<string, string>();
    public string? ActiveVersion                               { get; init; }
    public string? DebugVersion                                { get; init; } = "debug";
    public bool   AutoStart                                    { get; init; } = true;
    public int?   Pid                                          { get; init; }
    public DateTimeOffset? LastStartedAt                       { get; init; }
}
