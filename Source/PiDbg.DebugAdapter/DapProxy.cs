using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PiDbg.DebugAdapter.Dap;

namespace PiDbg.DebugAdapter;

// After configurationDone, proxies DAP messages between VS Code (stdio) and vsdbg (TCP).
// Intercepts disconnect/terminate to allow the adapter to run teardown before forwarding.
internal sealed partial class DapProxy : IDisposable
{
    private readonly DapReader _fromVsCode;
    private readonly DapWriter _toVsCode;
    private readonly TcpClient _tcp;
    private readonly DapReader _fromVsdbg;
    private readonly DapWriter _toVsdbg;
    private readonly ILogger   _logger;

    private DapProxy(
        DapReader fromVsCode, DapWriter toVsCode,
        TcpClient tcp, DapReader fromVsdbg, DapWriter toVsdbg,
        ILogger logger)
    {
        _fromVsCode = fromVsCode;
        _toVsCode   = toVsCode;
        _tcp        = tcp;
        _fromVsdbg  = fromVsdbg;
        _toVsdbg    = toVsdbg;
        _logger     = logger;
    }

    public static async Task<DapProxy> ConnectAsync(
        DapReader fromVsCode, DapWriter toVsCode,
        string host, int port, ILogger logger, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);

        var netStream = tcp.GetStream();
        var fromVsdbg = new DapReader(netStream);
        var toVsdbg   = new DapWriter(netStream);

        return new DapProxy(fromVsCode, toVsCode, tcp, fromVsdbg, toVsdbg, logger);
    }

    // Send initialize+attach to vsdbg, consume those responses, forward vsdbg events to VS Code.
    // After this returns, the full proxy loop can start: VS Code setBreakpoints/configurationDone
    // messages flow through the proxy directly to vsdbg.
    public async Task HandshakeWithVsdbgAsync(int appPid, CancellationToken ct)
    {
        var init = DapMessage.BuildRequest(1, "initialize", new
        {
            clientID   = "pidbg",
            clientName = "PiDbg",
            adapterID  = "coreclr",
            locale     = "en-US",
            linesStartAt1   = true,
            columnsStartAt1 = true,
            pathFormat      = "path",
            supportsVariableType          = true,
            supportsVariablePaging        = true,
            supportsRunInTerminalRequest  = false,
            supportsMemoryReferences      = true,
            supportsProgressReporting     = true,
        });
        await _toVsdbg.WriteMessageAsync(init, ct).ConfigureAwait(false);

        var attach = DapMessage.BuildRequest(2, "attach", new { processId = appPid });
        await _toVsdbg.WriteMessageAsync(attach, ct).ConfigureAwait(false);

        // Drain vsdbg until we have responses for both initialize (req_seq=1) and attach (req_seq=2).
        // Forward events to VS Code; discard the two handshake responses (VS Code didn't send those requests).
        var pending = new HashSet<int> { 1, 2 };
        while (pending.Count > 0)
        {
            var json = await _fromVsdbg.ReadMessageAsync(ct).ConfigureAwait(false);
            if (json is null) throw new IOException("vsdbg disconnected during handshake");

            var msg = DapMessage.TryParse(json);
            if (msg is null) continue;

            if (msg.Type == "response")
            {
                pending.Remove(msg.RequestSeq);
                LogHandshakeResponse(_logger, msg.Command ?? "?", msg.RequestSeq);
            }
            else
            {
                // Forward events (initialized, output, module, thread …) straight to VS Code.
                await _toVsCode.WriteMessageAsync(json, ct).ConfigureAwait(false);
            }
        }
    }

    // Run the proxy loop. Returns when the session ends (disconnect or error).
    // onDisconnect is called before the disconnect message is forwarded to vsdbg.
    public async Task RunAsync(Func<Task> onDisconnect, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var vsCodeToVsdbg = ForwardAsync(_fromVsCode, _toVsdbg, onDisconnect, linked);
        var vsdbgToVsCode = ForwardAsync(_fromVsdbg, _toVsCode, onTeardown: null, linked);

        await Task.WhenAny(vsCodeToVsdbg, vsdbgToVsCode).ConfigureAwait(false);
        linked.Cancel();
        await Task.WhenAll(vsCodeToVsdbg, vsdbgToVsCode).ConfigureAwait(false);
    }

    private async Task ForwardAsync(
        DapReader source, DapWriter sink,
        Func<Task>? onTeardown,
        CancellationTokenSource stopSource)
    {
        try
        {
            while (!stopSource.IsCancellationRequested)
            {
                var json = await source.ReadMessageAsync(stopSource.Token).ConfigureAwait(false);
                if (json is null) break;

                var msg = DapMessage.TryParse(json);
                if (msg != null && onTeardown != null &&
                    (msg.Command == "disconnect" || msg.Command == "terminate"))
                {
                    LogTeardown(_logger, msg.Command);
                    try { await onTeardown().ConfigureAwait(false); } catch { /* best effort */ }
                }

                await sink.WriteMessageAsync(json, stopSource.Token).ConfigureAwait(false);

                if (msg?.Command == "disconnect" || msg?.Command == "terminate")
                    break;
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex) when (ex is IOException || ex is SocketException)
        {
            LogProxyChannelClosed(_logger, ex);
        }
        finally
        {
            stopSource.Cancel();
        }
    }

    public void Dispose() => _tcp.Dispose();

    [LoggerMessage(Level = LogLevel.Information, Message = "DAP {Command} received — running teardown")]
    private static partial void LogTeardown(ILogger logger, string command);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Proxy channel closed")]
    private static partial void LogProxyChannelClosed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "vsdbg handshake: got response for {Command} (request_seq={RequestSeq})")]
    private static partial void LogHandshakeResponse(ILogger logger, string command, int requestSeq);
}
