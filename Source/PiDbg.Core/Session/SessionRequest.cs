using PiDbg.Infrastructure;

namespace PiDbg.Core;

public sealed record SessionRequest
{
    public SshConnectionConfig Connection  { get; init; } = new SshConnectionConfig();
    public string              AppName     { get; init; } = "";
    public string              ProjectPath { get; init; } = "";
    public string[]            AppArgs     { get; init; } = System.Array.Empty<string>();
    public bool                DeployRuntimeIfNecessary { get; init; }
}
