using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace PiDbg.Infrastructure;

public sealed partial class SshConnectionManager : ISshConnectionManager, IDisposable
{
    private readonly ConcurrentDictionary<string, SshSession> _sessions =
        new ConcurrentDictionary<string, SshSession>(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<SshConnectionManager> _logger;

    public SshConnectionManager(ILogger<SshConnectionManager> logger)
    {
        _logger = logger;
    }

    public async Task<SshSession> ConnectAsync(SshConnectionConfig config, CancellationToken ct)
    {
        var key = $"{config.Host}:{config.Port}";

        if (_sessions.TryGetValue(key, out var existing) && existing.Ssh.IsConnected)
            return existing;

        AuthenticationMethod[] authMethods = config.KeyFile != null
            ? new AuthenticationMethod[]
              { new PrivateKeyAuthenticationMethod(config.User, new PrivateKeyFile(config.KeyFile)) }
            : new AuthenticationMethod[]
              { new PasswordAuthenticationMethod(config.User, config.Password ?? "") };

        var connInfo = new ConnectionInfo(config.Host, config.Port, config.User, authMethods);
        var session  = await ConnectWithRetryAsync(connInfo, config.Host, ct).ConfigureAwait(false);

        _sessions[key] = session;
        LogConnected(_logger, config.Host, config.Port, config.User);
        return session;
    }

    private async Task<SshSession> ConnectWithRetryAsync(
        ConnectionInfo connInfo, string host, CancellationToken ct)
    {
        const int maxAttempts = 3;
        const int backoffMs   = 2000;

        Exception? last = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var ssh  = new SshClient(connInfo);
                var sftp = new SftpClient(connInfo);
                await Task.Run(() => { ssh.Connect(); sftp.Connect(); }, ct).ConfigureAwait(false);
                return new SshSession(ssh, sftp, host);
            }
            catch (Exception ex) when (ex is SocketException || ex is SshException)
            {
                last = ex;
                LogConnectAttemptFailed(_logger, attempt, maxAttempts, host, ex.Message);

                if (attempt < maxAttempts)
                    await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
        }
        throw new InvalidOperationException(
            $"Failed to connect to {connInfo.Host} after {maxAttempts} attempts.", last);
    }

    public void Disconnect(string host)
    {
        var toRemove = _sessions.Keys
            .Where(k => k.StartsWith(host + ":", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var key in toRemove)
        {
            if (_sessions.TryRemove(key, out var session))
                session.Dispose();
        }
        LogDisconnected(_logger, host);
    }

    public SshSession? GetActiveSession(string host)
    {
        foreach (var kvp in _sessions)
        {
            if (kvp.Key.StartsWith(host + ":", StringComparison.OrdinalIgnoreCase)
                && kvp.Value.Ssh.IsConnected)
                return kvp.Value;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Dispose();
        _sessions.Clear();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to {Host}:{Port} as {User}")]
    private static partial void LogConnected(ILogger logger, string host, int port, string user);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SSH connect attempt {Attempt}/{Max} to {Host} failed: {Error}")]
    private static partial void LogConnectAttemptFailed(ILogger logger, int attempt, int max, string host, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disconnected from {Host}")]
    private static partial void LogDisconnected(ILogger logger, string host);
}
