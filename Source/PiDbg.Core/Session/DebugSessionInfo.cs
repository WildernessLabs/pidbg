using System;
using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.Core;

public sealed record DebugSessionInfo
{
    public int    LocalPort  { get; init; }
    public int    AppPid     { get; init; }
    public string AppDllPath { get; init; } = "";

    // Resumes the app if it was started suspended (StopAtEntry). Safe to call even
    // when the app wasn't suspended - it's then a harmless no-op on the daemon side.
    public Func<CancellationToken, Task> ResumeAsync { get; init; } = _ => Task.CompletedTask;
}
