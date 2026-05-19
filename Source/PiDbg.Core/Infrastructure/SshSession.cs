using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace PiDbg.Infrastructure;

public sealed class SshSession : IDisposable
{
    public SshClient  Ssh  { get; }
    public SftpClient Sftp { get; }
    public string     Host { get; }

    private readonly HashSet<string> _createdDirs = new HashSet<string>(StringComparer.Ordinal);
    private readonly object _dirLock = new object();

    internal SshSession(SshClient ssh, SftpClient sftp, string host)
    {
        Ssh  = ssh;
        Sftp = sftp;
        Host = host;
    }

    public Task<(int ExitCode, string Stdout, string Stderr)>
        ExecuteAsync(string command, CancellationToken ct)
        => ExecuteAsync(command, TimeSpan.FromSeconds(30), ct);

    public async Task<(int ExitCode, string Stdout, string Stderr)>
        ExecuteAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        using (var cmd = Ssh.CreateCommand(command))
        {
            cmd.CommandTimeout = timeout;
            await Task.Run(() => cmd.Execute(), ct).ConfigureAwait(false);
            return (cmd.ExitStatus ?? -1, cmd.Result, cmd.Error);
        }
    }

    public async Task UploadFileAsync(
        Stream source, string remotePath,
        IProgress<long>? progress, CancellationToken ct)
    {
        EnsureRemoteDir(LinuxDirName(remotePath));

        await Task.Run(() =>
        {
            Sftp.UploadFile(source, remotePath, canOverride: true,
                uploadCallback: uploaded => progress?.Report((long)uploaded));
        }, ct).ConfigureAwait(false);
    }

    public async Task<(ForwardedPortLocal Tunnel, int LocalPort)>
        OpenTunnelAsync(int remotePort, CancellationToken ct)
    {
        var fwd = new ForwardedPortLocal("127.0.0.1", 0u, "127.0.0.1", (uint)remotePort);
        Ssh.AddForwardedPort(fwd);
        fwd.Start();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (fwd.BoundPort == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, ct).ConfigureAwait(false);

        if (fwd.BoundPort == 0)
            throw new InvalidOperationException(
                $"SSH tunnel to remote port {remotePort} failed to bind a local port.");

        return (fwd, (int)fwd.BoundPort);
    }

    private void EnsureRemoteDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        var parts = dir.TrimStart('/').Split('/');
        var current = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part)) continue;
            current += "/" + part;

            bool added;
            lock (_dirLock)
                added = _createdDirs.Add(current);

            if (!added) continue;

            if (!Sftp.Exists(current))
            {
                try { Sftp.CreateDirectory(current); }
                catch { /* concurrent creation — ignore */ }
            }
        }
    }

    public string ExpandPath(string path)
    {
        var user = Ssh.ConnectionInfo.Username;
        if (path == "~")           return $"/home/{user}";
        if (path.StartsWith("~/")) return $"/home/{user}/{path.Substring(2)}";
        return path;
    }

    private static string LinuxDirName(string remotePath)
    {
        var idx = remotePath.LastIndexOf('/');
        return idx <= 0 ? "/" : remotePath.Substring(0, idx);
    }

    public void Dispose()
    {
        Sftp?.Dispose();
        Ssh?.Dispose();
    }
}
