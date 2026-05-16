using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

public sealed class VsdbgProcess : IDisposable
{
    public int     Pid    { get; init; }
    public int     Port   { get; init; }
    public Process Handle { get; init; } = null!;

    internal TcpListener? Listener  { get; set; }
    internal Task?        ProxyTask { get; set; }

    public void Dispose()
    {
        try { Listener?.Stop(); }  catch { }
        Handle.Dispose();
    }
}

internal class VsdbgLauncher
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VsdbgLauncher> _logger;

    public VsdbgLauncher(
        IOptions<DaemonOptions> options,
        ILogger<VsdbgLauncher> logger)
    {
        _options = options.Value;
        _logger  = logger;
    }

    public Task<VsdbgProcess> LaunchAsync(
        int port, int? attachPid, CancellationToken ct)
    {
        var vsdbgPath = DaemonPaths.VsdbgBinPath(_options);
        if (!File.Exists(vsdbgPath))
            throw new InvalidOperationException("vsdbg not installed");

        var info = new ProcessStartInfo(vsdbgPath)
        {
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        info.ArgumentList.Add("--interpreter=vscode");
        // --attach N makes vsdbg auto-attach once VS sends the DAP attach request.
        if (attachPid.HasValue)
        {
            info.ArgumentList.Add("--attach");
            info.ArgumentList.Add(attachPid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        var stderrBuf = new StringBuilder();
        var process   = new Process { StartInfo = info, EnableRaisingEvents = true };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                _logger.LogDebug("vsdbg: {Msg}", e.Data);
                stderrBuf.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginErrorReadLine();

        // Open TCP proxy listener on loopback — VS connects here via SSH tunnel.
        // vsdbg itself does not support a server/port mode; we bridge TCP to its stdin/stdout.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _logger.LogInformation("vsdbg proxy ready on port {Port} (vsdbg PID={Pid})", port, process.Id);

        var vsdbgProcess = new VsdbgProcess { Pid = process.Id, Port = port, Handle = process };
        vsdbgProcess.Listener  = listener;
        vsdbgProcess.ProxyTask = RunProxyAsync(listener, process, stderrBuf, ct);

        return Task.FromResult(vsdbgProcess);
    }

    private async Task RunProxyAsync(
        TcpListener listener, Process vsdbg, StringBuilder stderrBuf, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Waiting for debugger connection on proxy port...");
            using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            listener.Stop();

            _logger.LogInformation("Debugger connected — piping to vsdbg stdin/stdout");

            var net      = client.GetStream();
            var toVsdbg  = net.CopyToAsync(vsdbg.StandardInput.BaseStream, ct);
            var fromVsdbg = vsdbg.StandardOutput.BaseStream.CopyToAsync(net, ct);

            await Task.WhenAny(toVsdbg, fromVsdbg).ConfigureAwait(false);
            _logger.LogInformation("vsdbg proxy session ended");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "vsdbg proxy error. vsdbg stderr: {Stderr}", stderrBuf);
        }
        finally
        {
            try { listener.Stop(); } catch { }
            if (!vsdbg.HasExited)
            {
                try { vsdbg.Kill(entireProcessTree: true); } catch { }
            }
        }
    }

    public int AllocatePort()
    {
        for (var p = _options.VsdbgPortRangeStart; p <= _options.VsdbgPortRangeEnd; p++)
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, p);
                probe.Start();
                probe.Stop();
                return p;
            }
            catch (SocketException) { /* port in use */ }
        }
        throw new InvalidOperationException(
            $"No free vsdbg port in range {_options.VsdbgPortRangeStart}-{_options.VsdbgPortRangeEnd}");
    }
}
