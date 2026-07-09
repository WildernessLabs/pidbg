using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

public interface IProcessManager
{
    Task<StartProcessResult> StartAsync(string appName, CancellationToken ct);
    Task StopAsync(string appName, CancellationToken ct);
    Task<StartProcessResult> RestartAsync(string appName, CancellationToken ct);
    AppState GetState(string appName);
    int? GetPid(string appName);
    int? GetExitCode(string appName);
    IReadOnlyList<string> GetRecentOutput(string appName);
    ProcessOutputBroadcaster GetOutputBroadcaster(string appName);
    void ReconcileRunningProcess(string appName, int pid);
}

public record StartProcessResult(bool Success, int? Pid, string? Error);
