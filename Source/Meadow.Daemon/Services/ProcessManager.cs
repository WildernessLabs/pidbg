using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Contracts.V1;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.Services;

internal class ProcessManager : IProcessManager, IDisposable
{
    private readonly ConcurrentDictionary<string, ManagedProcess> _processes = new();
    private readonly IDeploymentManager _deploymentManager;
    private readonly StateStore _stateStore;
    private readonly DaemonOptions _options;
    private readonly ILogger<ProcessManager> _logger;

    public ProcessManager(
        IDeploymentManager deploymentManager,
        StateStore stateStore,
        IOptions<DaemonOptions> options,
        ILogger<ProcessManager> logger)
    {
        _deploymentManager = deploymentManager;
        _stateStore = stateStore;
        _options = options.Value;
        _logger = logger;
    }

    private sealed class ManagedProcess : IDisposable
    {
        public Process?                 Handle       { get; set; }
        public ProcessOutputBroadcaster Broadcaster  { get; } = new();
        public AppState                 State        { get; set; } = AppState.Stopped;
        public int                      RestartCount { get; set; }
        public DateTimeOffset           LastCrashAt  { get; set; }

        public void Dispose() => Broadcaster.Dispose();
    }

    public async Task<StartProcessResult> StartAsync(string appName, CancellationToken ct)
    {
        var state = await _stateStore.LoadAppsAsync(ct);
        var app = state.Apps.FirstOrDefault(a => a.Name == appName);
        if (app == null)
            return new StartProcessResult(false, null, $"App '{appName}' not registered");

        var managed = _processes.GetOrAdd(appName, _ => new ManagedProcess());
        
        // Don't start if already running
        if (managed.State == AppState.Running && managed.Handle != null && !managed.Handle.HasExited)
            return new StartProcessResult(true, managed.Handle.Id, null);

        managed.State = AppState.Starting;

        var debugDir = DaemonPaths.AppDebugDir(_options, appName);
        var entryPoint = Path.Combine(debugDir, app.EntryPoint.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(entryPoint))
        {
            managed.State = AppState.Failed;
            return new StartProcessResult(false, null, $"Entry point not found: {entryPoint}");
        }

        var info = new ProcessStartInfo(ResolveDotnetExecutable(), entryPoint)
        {
            WorkingDirectory       = debugDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (var kv in app.EnvironmentVariables)
            info.Environment[kv.Key] = kv.Value;

        if (!string.IsNullOrWhiteSpace(app.StartupArgs))
        {
            // Simple split by space. For more complex args we'd need a proper parser.
            foreach (var arg in app.StartupArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                info.ArgumentList.Add(arg);
        }

        try
        {
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    managed.Broadcaster.TryWrite(new OutputLine
                    {
                        Stream    = OutputStream.Stdout,
                        Text      = e.Data,
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
            };
            
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    managed.Broadcaster.TryWrite(new OutputLine
                    {
                        Stream    = OutputStream.Stderr,
                        Text      = e.Data,
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    });
            };
            
            process.Exited += (_, _) => OnProcessExited(appName, managed);

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            managed.Handle = process;
            managed.State  = AppState.Running;

            // Update persisted state
            app.Pid = process.Id;
            app.LastStartedAt = DateTimeOffset.UtcNow;
            await _stateStore.SaveAppsAsync(state, ct);

            _logger.LogInformation("Started app {App} PID={Pid}", appName, process.Id);
            return new StartProcessResult(true, process.Id, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start app {App}", appName);
            managed.State = AppState.Failed;
            return new StartProcessResult(false, null, ex.Message);
        }
    }

    public async Task StopAsync(string appName, CancellationToken ct)
    {
        if (!_processes.TryGetValue(appName, out var managed) || managed.Handle == null || managed.Handle.HasExited)
        {
            if (managed != null) managed.State = AppState.Stopped;
            return;
        }

        managed.State = AppState.Stopping;
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Graceful SIGTERM
                Mono.Unix.Native.Syscall.kill(managed.Handle.Id, Mono.Unix.Native.Signum.SIGTERM);
            }
            else
            {
                managed.Handle.Kill();
            }

            // Wait for graceful exit
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ProcessGracefulStopTimeout);

            try
            {
                await managed.Handle.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("App {App} PID={Pid} failed to exit gracefully; killing", appName, managed.Handle.Id);
                managed.Handle.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { /* already exited */ }
        
        managed.State = AppState.Stopped;
    }

    public async Task<StartProcessResult> RestartAsync(string appName, CancellationToken ct)
    {
        await StopAsync(appName, ct);
        return await StartAsync(appName, ct);
    }

    public AppState GetState(string appName)
    {
        if (_processes.TryGetValue(appName, out var managed))
            return managed.State;
        return AppState.Stopped;
    }

    public int? GetPid(string appName)
    {
        if (_processes.TryGetValue(appName, out var managed) && managed.Handle != null && !managed.Handle.HasExited)
            return managed.Handle.Id;
        return null;
    }

    public ProcessOutputBroadcaster GetOutputBroadcaster(string appName)
    {
        var managed = _processes.GetOrAdd(appName, _ => new ManagedProcess());
        return managed.Broadcaster;
    }

    public void ReconcileRunningProcess(string appName, int pid)
    {
        var managed = _processes.GetOrAdd(appName, _ => new ManagedProcess());
        try
        {
            var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                managed.Handle = process;
                managed.State = AppState.Running;
                // Note: we can't easily attach to stdout/stderr of an already running process
                // without redirecting it at startup. Reconciled processes won't have output streaming
                // unless we use something like gdb or reptyr (out of scope).
            }
        }
        catch { /* process not found or no access */ }
    }

    private static string ResolveDotnetExecutable()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var candidate = Path.Combine(dotnetRoot, "dotnet");
            if (File.Exists(candidate)) return candidate;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var homeCandidate = Path.Combine(home, ".dotnet", "dotnet");
        if (File.Exists(homeCandidate)) return homeCandidate;
        return "dotnet";
    }

    private void OnProcessExited(string appName, ManagedProcess managed)
    {
        managed.State = AppState.Stopped;
        _logger.LogInformation("App {App} exited with code {Code}",
            appName, managed.Handle?.ExitCode);
    }

    public void Dispose()
    {
        foreach (var managed in _processes.Values)
            managed.Dispose();
        _processes.Clear();
    }
}
