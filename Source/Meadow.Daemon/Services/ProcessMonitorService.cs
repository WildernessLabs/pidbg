using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Models;
using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

public sealed class ProcessMonitorService : BackgroundService
{
    private readonly IProcessManager _processManager;
    private readonly IServiceProvider _services; // Use ServiceProvider to resolve IDebugSessionManager lazily
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _options;
    private readonly ILogger<ProcessMonitorService> _logger;

    // Crash loop detection: per-app ring buffer of recent restart timestamps
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _restartHistory = new();
    private const int CrashLoopThreshold  = 5;
    private static readonly TimeSpan CrashLoopWindow = TimeSpan.FromMinutes(1);

    public ProcessMonitorService(
        IProcessManager processManager,
        IServiceProvider services,
        StateStore stateStore,
        IOptions<DaemonOptions> options,
        ILogger<ProcessMonitorService> logger)
    {
        _processManager = processManager;
        _services = services;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Process Monitor Service starting");

        // Startup reconciliation: match StateStore PIDs against /proc
        await ReconcileProcessesAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await MonitorCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Process Monitor cycle");
            }
        }
    }

    private async Task ReconcileProcessesAsync(CancellationToken ct)
    {
        var state = await _stateStore.LoadAppsAsync(ct);
        bool changed = false;

        foreach (var app in state.Apps.Where(a => a.Pid.HasValue))
        {
            var pid = app.Pid!.Value;
            if (IsProcessAlive(pid) && GetProcessCmdline(pid).Contains(app.EntryPoint))
            {
                _processManager.ReconcileRunningProcess(app.Name, pid);
                _logger.LogInformation("Reconciled running app {App} PID={Pid}", app.Name, pid);
            }
            else
            {
                _logger.LogWarning("App {App} PID={Pid} not found or mismatch; clearing stale PID", app.Name, pid);
                app.Pid = null;
                changed = true;
            }
        }

        if (changed)
            await _stateStore.SaveAppsAsync(state, ct);
    }

    private async Task MonitorCycleAsync(CancellationToken ct)
    {
        var state = await _stateStore.LoadAppsAsync(ct);
        foreach (var app in state.Apps)
        {
            var currentState = _processManager.GetState(app.Name);
            if (currentState == AppState.Running || currentState == AppState.Starting || currentState == AppState.Stopping) 
                continue;

            // Only auto-restart if configured
            if (!app.AutoStart && !_options.AutoRestartManagedApp) continue;
            
            if (IsInCrashLoop(app.Name))
            {
                _logger.LogCritical("App {App} is in a crash loop and will not be auto-restarted", app.Name);
                continue;
            }

            _logger.LogWarning("App {App} is {State}; attempting restart", app.Name, currentState);
            RecordRestart(app.Name);
            await _processManager.StartAsync(app.Name, ct);
        }

        // Expire orphaned debug sessions
        await ExpireOrphanSessionsAsync(ct);
    }

    private bool IsInCrashLoop(string appName)
    {
        var history = _restartHistory.GetOrAdd(appName, _ => new Queue<DateTimeOffset>());
        var cutoff  = DateTimeOffset.UtcNow - CrashLoopWindow;
        
        while (history.TryPeek(out var oldest) && oldest < cutoff)
            history.Dequeue();
            
        if (history.Count < CrashLoopThreshold) return false;

        _logger.LogError("Crash loop detected for {App}: {Count} restarts in {Window}",
            appName, history.Count, CrashLoopWindow);
        return true;
    }

    private void RecordRestart(string appName)
    {
        var history = _restartHistory.GetOrAdd(appName, _ => new Queue<DateTimeOffset>());
        history.Enqueue(DateTimeOffset.UtcNow);
    }

    private static bool IsProcessAlive(int pid)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return Directory.Exists($"/proc/{pid}");
        
        try { Process.GetProcessById(pid); return true; }
        catch { return false; }
    }

    private static string GetProcessCmdline(int pid)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return ""; // Not easily available on Windows without WMI/etc.

        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' '); }
        catch { return ""; }
    }

    private async Task ExpireOrphanSessionsAsync(CancellationToken ct)
    {
        var sessionManager = _services.GetService<IDebugSessionManager>();
        if (sessionManager == null) return;

        var sessions = await _stateStore.LoadSessionsAsync(ct);
        var now      = DateTimeOffset.UtcNow;
        var timeout  = _options.DebugSessionOrphanTimeout;
        
        var expired  = sessions.Sessions
            .Where(s => s.State == SessionState.Active // READY in spec, using ACTIVE from proto
                     && now - s.LastActivityAt > timeout)
            .ToList();

        foreach (var session in expired)
        {
            _logger.LogWarning("Debug session {Id} orphaned (idle {Minutes}m); stopping",
                session.SessionId, (int)(now - session.LastActivityAt).TotalMinutes);
            await sessionManager.StopDebugSessionAsync(session.SessionId, ct);
        }
    }
}
