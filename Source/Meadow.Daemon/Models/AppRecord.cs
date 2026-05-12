namespace Meadow.Daemon.Models;

public sealed record AppRecord
{
    public string Name                                         { get; set; } = "";
    public string EntryPoint                                   { get; set; } = "";
    public string? StartupArgs                                 { get; set; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; set; }
        = new Dictionary<string, string>();
    public string? ActiveVersion                               { get; set; }
    public string? DebugVersion                                { get; set; } = "debug";
    public bool   AutoStart                                    { get; set; } = true;
    public int?   Pid                                          { get; set; }
    public DateTimeOffset? LastStartedAt                       { get; set; }
}
