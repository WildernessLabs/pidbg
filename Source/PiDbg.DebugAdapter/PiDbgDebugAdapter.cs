using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PiDbg.Build;
using PiDbg.Core;
using PiDbg.DebugAdapter.Dap;
using PiDbg.Infrastructure;

namespace PiDbg.DebugAdapter;

// Handles the DAP conversation with VS Code until the proxy takes over.
internal sealed class PiDbgDebugAdapter
{
    private readonly DapReader _reader;
    private readonly DapWriter _writer;
    private readonly SessionOrchestrator _orchestrator;
    private readonly ILogger<PiDbgDebugAdapter> _logger;

    private int _seq;

    public PiDbgDebugAdapter(
        DapReader reader, DapWriter writer,
        SessionOrchestrator orchestrator,
        ILogger<PiDbgDebugAdapter> logger)
    {
        _reader       = reader;
        _writer       = writer;
        _orchestrator = orchestrator;
        _logger       = logger;
    }

    // Returns DebugSessionInfo once session is ready and proxy should take over.
    // Returns null on disconnect before session started.
    public async Task<(DebugSessionInfo? Info, DapMessage? PendingDisconnect)> RunHandshakeAsync(
        CancellationToken ct)
    {
        DebugSessionInfo? sessionInfo = null;

        while (true)
        {
            var json = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
            if (json is null) return (null, null);

            var msg = DapMessage.TryParse(json);
            if (msg is null) continue;

            _logger.LogDebug("← {Type} {Command}", msg.Type, msg.Command ?? msg.Event);

            if (msg.Type == "request")
            {
                switch (msg.Command)
                {
                    case "initialize":
                        await HandleInitializeAsync(msg, ct).ConfigureAwait(false);
                        break;

                    case "launch":
                        sessionInfo = await HandleLaunchAsync(msg, ct).ConfigureAwait(false);
                        if (sessionInfo is null) return (null, null); // launch failed
                        break;

                    case "setBreakpoints":
                    case "setFunctionBreakpoints":
                    case "setExceptionBreakpoints":
                    case "configurationDone":
                        // Ack these — vsdbg will handle the real breakpoint setup via proxy
                        await SendResponseAsync(msg.Seq, msg.Command!, success: true, ct: ct)
                            .ConfigureAwait(false);

                        if (msg.Command == "configurationDone")
                            return (sessionInfo, null); // hand off to proxy
                        break;

                    case "disconnect":
                    case "terminate":
                        await SendResponseAsync(msg.Seq, msg.Command!, success: true, ct: ct)
                            .ConfigureAwait(false);
                        return (null, msg);

                    default:
                        await SendResponseAsync(msg.Seq, msg.Command!, success: false, ct: ct,
                            body: new { error = new { id = 1, format = $"Unknown command: {msg.Command}" } })
                            .ConfigureAwait(false);
                        break;
                }
            }
        }
    }

    private async Task HandleInitializeAsync(DapMessage msg, CancellationToken ct)
    {
        var capabilities = new
        {
            supportsConfigurationDoneRequest = true,
            supportsTerminateRequest         = true,
            supportTerminateDebuggee         = true,
        };
        await SendResponseAsync(msg.Seq, "initialize", success: true, body: capabilities, ct: ct)
            .ConfigureAwait(false);

        // Send initialized event — VS Code will now send breakpoint configs
        await SendEventAsync("initialized", body: (object?)null, ct).ConfigureAwait(false);
    }

    private async Task<DebugSessionInfo?> HandleLaunchAsync(DapMessage msg, CancellationToken ct)
    {
        var body = msg.Body;
        if (body is null)
        {
            await SendErrorAsync(msg.Seq, "launch", "Missing launch arguments.", ct).ConfigureAwait(false);
            return null;
        }

        string Get(string key, string defaultValue = "")
            => body[key]?.GetValue<string>() ?? defaultValue;

        var request = new SessionRequest
        {
            Connection = new SshConnectionConfig
            {
                Host       = Get("host"),
                User       = Get("username"),
                Port       = body["port"]?.GetValue<int>() ?? 22,
                KeyFile    = Get("privateKeyPath") is { Length: > 0 } k ? k : null,
                Password   = Get("password")   is { Length: > 0 } p ? p : null,
                RootFolder = Get("rootFolder", "~/meadow"),
            },
            AppName     = Get("appName"),
            ProjectPath = Get("projectPath"),
        };

        if (string.IsNullOrEmpty(request.Connection.Host) ||
            string.IsNullOrEmpty(request.AppName)         ||
            string.IsNullOrEmpty(request.ProjectPath))
        {
            await SendErrorAsync(msg.Seq, "launch",
                "launch.json must include host, appName, and projectPath.", ct)
                .ConfigureAwait(false);
            return null;
        }

        // Ack launch immediately; progress goes to Debug Console via output events
        await SendResponseAsync(msg.Seq, "launch", success: true, ct: ct).ConfigureAwait(false);

        var progress = new Progress<string>(line =>
            _ = SendEventAsync("output", new { category = "console", output = line + "\n" }, ct));

        try
        {
            var info = await _orchestrator.RunAsync(request, progress, ct).ConfigureAwait(false);
            await SendEventAsync("process", new { name = request.AppName, systemProcessId = info.AppPid }, ct)
                .ConfigureAwait(false);
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session orchestration failed");
            await SendEventAsync("output",
                new { category = "stderr", output = $"PiDbg error: {ex.Message}\n" }, ct)
                .ConfigureAwait(false);
            await SendEventAsync("terminated", body: (object?)null, ct).ConfigureAwait(false);
            return null;
        }
    }

    private Task SendResponseAsync(
        int requestSeq, string command, bool success,
        object? body = null, CancellationToken ct = default)
    {
        var json = DapMessage.BuildResponse(requestSeq, NextSeq(), command, success, body);
        _logger.LogDebug("→ response {Command} success={Success}", command, success);
        return _writer.WriteMessageAsync(json, ct);
    }

    private Task SendErrorAsync(int requestSeq, string command, string message, CancellationToken ct)
        => SendResponseAsync(requestSeq, command, success: false,
            body: new { error = new { id = 1, format = message } }, ct: ct);

    private Task SendEventAsync(string eventName, object? body, CancellationToken ct)
    {
        var json = DapMessage.BuildEvent(NextSeq(), eventName, body);
        _logger.LogDebug("→ event {Event}", eventName);
        return _writer.WriteMessageAsync(json, ct);
    }

    private int NextSeq() => Interlocked.Increment(ref _seq);
}
