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
        string appName, SessionMode mode, string correlationId, CancellationToken ct)
    {
        // Ensure app is running (start if not)
        if (_processManager.GetState(appName) != AppState.Running)
        {
            var startResult = await _processManager.StartAsync(appName, ct);
            if (!startResult.Success)
                throw new InvalidOperationException($"App '{appName}' failed to start: {startResult.Error}");
        }

        var appPid = _processManager.GetPid(appName)
            ?? throw new InvalidOperationException($"App '{appName}' failed to start (PID is null)");

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
}
