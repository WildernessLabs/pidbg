namespace PiDbg.Core;

public sealed record DebugSessionInfo
{
    public int    LocalPort  { get; init; }
    public int    AppPid     { get; init; }
    public string AppDllPath { get; init; } = "";
}
