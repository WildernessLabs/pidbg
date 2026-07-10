using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    private const int RecentOutputCapacity = 50;

    private sealed class ManagedProcess : IDisposable
    {
        public Process?                 Handle       { get; set; }
        public ProcessOutputBroadcaster Broadcaster  { get; } = new();
        public AppState                 State        { get; set; } = AppState.Stopped;
        public int                      RestartCount { get; set; }
        public DateTimeOffset           LastCrashAt  { get; set; }
        public bool                     Suspended    { get; set; }

        // Best-effort ring buffer of recent stdout/stderr lines, kept independent of the
        // pub/sub Broadcaster so crash/startup output is still available to a caller
        // that only subscribes (StreamOutput, GetRecentOutput) after the app already
        // produced it - e.g. an error during Initialize(), which happens well before
        // any client has had a chance to start streaming.
        public ConcurrentQueue<OutputLine> RecentOutput { get; } = new();

        public void AppendRecentOutput(OutputLine line)
        {
            RecentOutput.Enqueue(line);
            while (RecentOutput.Count > RecentOutputCapacity)
                RecentOutput.TryDequeue(out _);
        }

        public void Dispose() => Broadcaster.Dispose();
    }

    public async Task<StartProcessResult> StartAsync(string appName, CancellationToken ct, bool suspendOnStart = false)
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
        managed.RecentOutput.Clear();
        managed.Suspended = false;

        var debugDir = DaemonPaths.AppDebugDir(_options, appName);
        var entryPoint = Path.Combine(debugDir, app.EntryPoint.Replace('/', Path.DirectorySeparatorChar));

        if (!File.Exists(entryPoint))
        {
            managed.State = AppState.Failed;
            return new StartProcessResult(false, null, $"Entry point not found: {entryPoint}");
        }

        var dotnetExecutable = ResolveDotnetExecutable();
        var compatError = CheckRuntimeCompatibility(entryPoint, dotnetExecutable);
        if (compatError != null)
        {
            managed.State = AppState.Failed;
            return new StartProcessResult(false, null, compatError);
        }

        var info = new ProcessStartInfo(dotnetExecutable, entryPoint)
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
                {
                    var line = new OutputLine
                    {
                        Stream    = OutputStream.Stdout,
                        Text      = e.Data,
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    managed.AppendRecentOutput(line);
                    managed.Broadcaster.TryWrite(line);
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    var line = new OutputLine
                    {
                        Stream    = OutputStream.Stderr,
                        Text      = e.Data,
                        TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    managed.AppendRecentOutput(line);
                    managed.Broadcaster.TryWrite(line);
                }
            };
            
            process.Exited += (_, _) => OnProcessExited(appName, managed);

            process.Start();

            // Suspend as close to fork/exec as possible so debugger attach + breakpoint
            // binding happens before any of the app's own code runs. Not a hard guarantee
            // (a handful of runtime-startup instructions may execute before the signal is
            // delivered), but catches user code (Main/Initialize) reliably in practice.
            if (suspendOnStart && RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Mono.Unix.Native.Syscall.kill(process.Id, Mono.Unix.Native.Signum.SIGSTOP);
                managed.Suspended = true;
            }

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

    public Task<bool> ResumeAsync(string appName, CancellationToken ct)
    {
        if (!_processes.TryGetValue(appName, out var managed) || managed.Handle == null || managed.Handle.HasExited)
            return Task.FromResult(false);

        if (!managed.Suspended)
            return Task.FromResult(true); // not suspended - nothing to do, not an error

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Mono.Unix.Native.Syscall.kill(managed.Handle.Id, Mono.Unix.Native.Signum.SIGCONT);

        managed.Suspended = false;
        return Task.FromResult(true);
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

    public int? GetExitCode(string appName)
    {
        if (_processes.TryGetValue(appName, out var managed) && managed.Handle != null && managed.Handle.HasExited)
            return managed.Handle.ExitCode;
        return null;
    }

    public IReadOnlyList<OutputLine> GetRecentOutput(string appName)
    {
        if (_processes.TryGetValue(appName, out var managed))
            return managed.RecentOutput.ToArray();
        return Array.Empty<OutputLine>();
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

    // Best-effort pre-flight check: does an installed shared runtime on this device
    // satisfy what the app's own runtimeconfig.json requires? Catches version
    // mismatches (e.g. app built for net10.0, device only has .NET 8 installed) with
    // a clear, actionable message instead of a "framework not found" crash a few
    // hundred milliseconds into the debug session.
    private static string? CheckRuntimeCompatibility(string entryPoint, string dotnetExecutable)
    {
        var runtimeConfigPath = Path.ChangeExtension(entryPoint, null) + ".runtimeconfig.json";
        if (!File.Exists(runtimeConfigPath))
            return null; // Nothing to check against - don't block the launch.

        var dotnetDir = Path.GetDirectoryName(dotnetExecutable);
        if (string.IsNullOrEmpty(dotnetDir) || dotnetExecutable == "dotnet")
            return null; // Couldn't resolve an actual install location - can't verify.

        List<(string Name, string Version)> required;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
                return null;

            required = new List<(string, string)>();

            if (runtimeOptions.TryGetProperty("framework", out var single))
                AddFramework(single, required);

            if (runtimeOptions.TryGetProperty("frameworks", out var many) && many.ValueKind == JsonValueKind.Array)
                foreach (var fx in many.EnumerateArray())
                    AddFramework(fx, required);
        }
        catch (JsonException)
        {
            return null; // Malformed/unexpected shape - don't block on a parsing issue.
        }

        var missing = new List<string>();
        foreach (var (name, versionText) in required)
        {
            if (!Version.TryParse(versionText, out var requiredVersion))
                continue;

            var sharedDir = Path.Combine(dotnetDir, "shared", name);
            var installed = Directory.Exists(sharedDir)
                ? Directory.GetDirectories(sharedDir)
                    .Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null)
                    .Where(v => v != null)
                    .Cast<Version>()
                    .ToList()
                : new List<Version>();

            var satisfied = installed.Any(v => v.Major == requiredVersion.Major && v >= requiredVersion);
            if (!satisfied)
            {
                var installedText = installed.Count > 0
                    ? string.Join(", ", installed.OrderByDescending(v => v))
                    : "none";
                missing.Add($"{name} {versionText} (installed: {installedText})");
            }
        }

        if (missing.Count == 0)
            return null;

        return $"Required .NET runtime not installed on device: {string.Join("; ", missing)}. " +
               "Install the matching .NET runtime on the Pi, or retarget the project to a version already installed there.";
    }

    private static void AddFramework(JsonElement element, List<(string Name, string Version)> into)
    {
        if (element.TryGetProperty("name", out var name) && element.TryGetProperty("version", out var version))
        {
            var n = name.GetString();
            var v = version.GetString();
            if (!string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(v))
                into.Add((n, v));
        }
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
