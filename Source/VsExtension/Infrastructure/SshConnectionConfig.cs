namespace PiDbg.Infrastructure;

public sealed record SshConnectionConfig
{
    public string Host       { get; set; }
    public string User       { get; set; }
    public int    Port       { get; set; } = 22;
    public string RootFolder { get; set; } = "~/meadow";
    public string? KeyFile   { get; set; } = null;
    public string? Password  { get; set; } = null;
}
