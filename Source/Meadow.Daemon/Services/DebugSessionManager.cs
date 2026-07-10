using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.Services;

internal class DebugSessionManager : IDebugSessionManager
{
    private readonly IProcessManager _processManager;
    private readonly VsdbgLauncher _vsdbgLauncher;
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _options;
    private readonly ILogger<DebugSessionManager> _logger;

    // Active vsdbg processes keyed by sessionId
    private readonly ConcurrentDictionary<string, VsdbgProcess> _vsdbgProcesses = new();

    public DebugSessionManager(
        VsdbgLauncher vsdbgLauncher,
        IProcessManager processManager,
        StateStore stateStore,
        IOptions<DaemonOptions> options,
        ILogger<DebugSessionManager> logger)
    {
        _vsdbgLauncher = vsdbgLauncher;
        _processManager = processManager;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<DebugSessionRecord> StartDebugSessionAsync(
        string appName, SessionMode mode, string correlationId, CancellationToken ct,
        bool suspendOnStart = false)
    {
        // Ensure app is running (start if not)
        int appPid;
        if (_processManager.GetState(appName) != AppState.Running)
        {
            var startResult = await _processManager.StartAsync(appName, ct, suspendOnStart);
            if (!startResult.Success)
                throw new InvalidOperationException($"App '{appName}' failed to start: {startResult.Error}");

            appPid = startResult.Pid
                ?? throw new InvalidOperationException($"App '{appName}' started but no PID was returned.");

            // The process can exit immediately after starting (e.g. an unhandled
            // exception during startup) - report that clearly, including the exit
            // code and captured output, instead of losing the signal behind a
            // generic "no PID" error.
            if (_processManager.GetPid(appName) is null)
                throw new InvalidOperationException(BuildCrashMessage(appName, appPid));
        }
        else
        {
            appPid = _processManager.GetPid(appName)
                ?? throw new InvalidOperationException($"App '{appName}' is marked as running but no active process was found.");
        }

        // A brief grace period, then one more liveness check before handing off to
        // vsdbg: a fast-crashing app (e.g. a hardware/permissions failure during
        // startup) can die in the gap between the check above and vsdbg's attach,
        // which otherwise surfaces only as vsdbg's generic "process has been
        // terminated" with no indication of the real cause.
        await Task.Delay(300, ct).ConfigureAwait(false);
        if (_processManager.GetPid(appName) is null)
            throw new InvalidOperationException(BuildCrashMessage(appName, appPid));

        // Allocate port and launch vsdbg
        var port     = _vsdbgLauncher.AllocatePort();
        var vsdbg    = await _vsdbgLauncher.LaunchAsync(
            port, mode == SessionMode.Attach ? appPid : null, ct);

        // Create session record
        var sessionId = NewUlid();
        var record = new DebugSessionRecord
        {
            SessionId     = sessionId,
            AppName       = appName,
            VsdbgPid      = vsdbg.Pid,
            VsdbgPort     = port,
            AppPid        = appPid,
            Mode          = mode,
            State         = SessionState.Active,
            CorrelationId = correlationId,
            LastActivityAt = DateTimeOffset.UtcNow
        };

        _vsdbgProcesses[sessionId] = vsdbg;

        // Persist
        var state = await _stateStore.LoadSessionsAsync(ct);
        state.Sessions.Add(record);
        await _stateStore.SaveSessionsAsync(state, ct);

        _logger.LogInformation(
            "Debug session {Id} started for {App} on port {Port} (PID={VsdbgPid})",
            sessionId, appName, port, vsdbg.Pid);
            
        return record;
    }

    public Task<bool> ResumeAppAsync(string appName, CancellationToken ct)
        => _processManager.ResumeAsync(appName, ct);

    public async Task StopDebugSessionAsync(string sessionId, CancellationToken ct)
    {
        if (_vsdbgProcesses.TryRemove(sessionId, out var vsdbg))
        {
            try
            {
                vsdbg.Handle.Kill(entireProcessTree: true);
                await vsdbg.Handle.WaitForExitAsync(ct);
            }
            catch { /* vsdbg already exited */ }
            vsdbg.Dispose();
        }

        // Update state in store
        var state = await _stateStore.LoadSessionsAsync(ct);
        var session = state.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is not null)
        {
            session.State = SessionState.Ended;
            await _stateStore.SaveSessionsAsync(state, ct);
        }

        _logger.LogInformation("Debug session {Id} stopped", sessionId);
    }

    public async Task<DebugSessionRecord?> GetSessionStatusAsync(string sessionId, CancellationToken ct)
    {
        var state = await _stateStore.LoadSessionsAsync(ct);
        return state.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
    }

    public async Task<IReadOnlyList<DebugSessionRecord>> ListSessionsAsync(CancellationToken ct)
    {
        var state = await _stateStore.LoadSessionsAsync(ct);
        return state.Sessions;
    }

    public async Task TouchSessionAsync(string sessionId, CancellationToken ct)
    {
        var state   = await _stateStore.LoadSessionsAsync(ct);
        var session = state.Sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return;
        
        session.LastActivityAt = DateTimeOffset.UtcNow;
        await _stateStore.SaveSessionsAsync(state, ct);
    }

    private static string NewUlid()
    {
        // Simple placeholder for ULID: lexicographically sortable timestamp + random
        // For actual production we'd use a ULID library.
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("D15", CultureInfo.InvariantCulture) + Guid.NewGuid().ToString("N")[..10];
    }

    private string BuildCrashMessage(string appName, int appPid)
    {
        var exitCode = _processManager.GetExitCode(appName);
        var output = _processManager.GetRecentOutput(appName);

        var message = exitCode is null
            ? $"App '{appName}' exited immediately after starting (PID {appPid})."
            : $"App '{appName}' exited immediately after starting (PID {appPid}, exit code {exitCode}).";

        if (output.Count > 0)
        {
            var formatted = output.Select(l =>
                $"[{(l.Stream == OutputStream.Stderr ? "stderr" : "stdout")}] {l.Text}");
            message += "\nRecent output:\n" + string.Join('\n', formatted);
        }
        else
        {
            message += " No output was captured before it exited.";
        }

        return message;
    }
}
