using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

public sealed class VsdbgProcess : IDisposable
{
    public int     Pid    { get; init; }
    public int     Port   { get; init; }
    public Process Handle { get; init; } = null!;
    public void Dispose() => Handle.Dispose();
}

internal class VsdbgLauncher
{
    private readonly VsdbgManager _vsdbgManager;
    private readonly DaemonOptions _options;
    private readonly ILogger<VsdbgLauncher> _logger;

    public VsdbgLauncher(
        VsdbgManager vsdbgManager,
        IOptions<DaemonOptions> options,
        ILogger<VsdbgLauncher> logger)
    {
        _vsdbgManager = vsdbgManager;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<VsdbgProcess> LaunchAsync(
        int port, int? attachPid, CancellationToken ct)
    {
        var vsdbgPath = DaemonPaths.VsdbgBinPath(_options);
        if (!File.Exists(vsdbgPath))
            throw new InvalidOperationException("vsdbg not installed");

        var args = $"--server --port {port}";
        if (attachPid.HasValue)
            args += $" --attach {attachPid.Value}";

        _logger.LogInformation("Launching vsdbg: {Args}", args);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(vsdbgPath, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        // Capture vsdbg stderr for diagnostics
        var stderrBuf = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                _logger.LogDebug("vsdbg stderr: {Msg}", e.Data);
                stderrBuf.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        try
        {
            // Wait for vsdbg to be LISTEN on the port
            await WaitForPortAsync(port, timeout: TimeSpan.FromSeconds(10), ct);
            return new VsdbgProcess { Pid = process.Id, Port = port, Handle = process };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "vsdbg failed to start listening on port {Port}. Stderr: {Stderr}", port, stderrBuf.ToString());
            try { process.Kill(true); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Polls /proc/net/tcp and /proc/net/tcp6 every 250ms until the port is in LISTEN state.
    /// </summary>
    private async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var hexPort = port.ToString("X4", CultureInfo.InvariantCulture);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (IsPortListening(hexPort))
            {
                _logger.LogInformation("vsdbg listening on port {Port}", port);
                return;
            }

            await Task.Delay(250, ct);
        }

        throw new TimeoutException(
            $"vsdbg did not start listening on port {port} within {timeout.TotalSeconds}s");
    }

    private static bool IsPortListening(string hexPort)
    {
        // 0A = TCP_LISTEN
        return CheckProcTcpFile("/proc/net/tcp", hexPort) || CheckProcTcpFile("/proc/net/tcp6", hexPort);
    }

    private static bool CheckProcTcpFile(string path, string hexPort)
    {
        try
        {
            if (!File.Exists(path)) return false;
            
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4) continue;
                
                var localAddress = parts[1];
                var localPort = localAddress.Split(':').Last().ToUpperInvariant();
                var state     = parts[3];
                
                if (localPort == hexPort && state == "0A")
                    return true;
            }
        }
        catch { }
        return false;
    }

    public int AllocatePort()
    {
        // Find a free port in the configured range
        for (var p = _options.VsdbgPortRangeStart; p <= _options.VsdbgPortRangeEnd; p++)
        {
            if (!IsPortListening(p.ToString("X4", CultureInfo.InvariantCulture)))
                return p;
        }
        throw new InvalidOperationException(
            $"No free vsdbg port in range {_options.VsdbgPortRangeStart}-{_options.VsdbgPortRangeEnd}");
    }
}
