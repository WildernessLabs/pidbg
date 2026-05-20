using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
#if NET472
using System.Security.AccessControl;
using System.Security.Principal;
#endif
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal sealed class SshKeyPair
{
    public string PrivateKeyPath { get; set; } = "";
    public string PublicKey      { get; set; } = "";
}

internal static class SshAuthManager
{
    private static readonly string KeyDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiDbg", "ssh");

    private static readonly string ProfilesDir =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiDbg");

    private static readonly JsonSerializerOptions JsonWriteOpts =
        new JsonSerializerOptions { WriteIndented = true };

    public static async Task<SshKeyPair> EnsureKeyPairAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(KeyDir);
        var privPath = System.IO.Path.Combine(KeyDir, "id_pidbg");
        var pubPath  = System.IO.Path.Combine(KeyDir, "id_pidbg.pub");

        if (File.Exists(privPath) && File.Exists(pubPath))
            return new SshKeyPair
            {
                PrivateKeyPath = privPath,
                PublicKey      = File.ReadAllText(pubPath).Trim(),
            };

        await GenerateKeyPairCoreAsync(privPath, pubPath, ct).ConfigureAwait(false);
        return new SshKeyPair
        {
            PrivateKeyPath = privPath,
            PublicKey      = File.ReadAllText(pubPath).Trim(),
        };
    }

    public static Task<SshKeyPair> GenerateKeyPairAsync(CancellationToken ct)
        => EnsureKeyPairAsync(ct);

    public static async Task InstallPublicKeyAsync(
        SshSession session, SshKeyPair keyPair, CancellationToken ct)
    {
        var keyBlob  = keyPair.PublicKey.Split(' ').Length >= 2
            ? keyPair.PublicKey.Split(' ')[1] : keyPair.PublicKey;
        var checkCmd = $"grep -qF '{keyBlob}' ~/.ssh/authorized_keys 2>/dev/null && echo YES || echo NO";
        var (_, stdout, _) = await session.ExecuteAsync(checkCmd, ct).ConfigureAwait(false);

        if (stdout.Trim() == "YES") return;

        var escaped = keyPair.PublicKey.Replace("'", "'\\''");
        var (rc, _, err) = await session.ExecuteAsync(
            $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && " +
            $"echo '{escaped}' >> ~/.ssh/authorized_keys && " +
            $"chmod 600 ~/.ssh/authorized_keys",
            ct).ConfigureAwait(false);

        if (rc != 0)
            throw new ProvisioningException($"Failed to install SSH key: {err}");
    }

    public static Task InstallPublicKeyAsync(string publicKey, CancellationToken ct)
        => Task.CompletedTask;

    public static SshConnectionConfig? LoadStoredConfig(string host)
    {
        Directory.CreateDirectory(ProfilesDir);
        var path = System.IO.Path.Combine(ProfilesDir, $"profile_{SanitizeHost(host)}.json");
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<SshConnectionConfig>(json);
        }
        catch { return null; }
    }

    public static void SaveConfig(string host, SshConnectionConfig config)
    {
        Directory.CreateDirectory(ProfilesDir);
        var path = System.IO.Path.Combine(ProfilesDir, $"profile_{SanitizeHost(host)}.json");
        var json = JsonSerializer.Serialize(config, JsonWriteOpts);
        File.WriteAllText(path, json);
    }

    public static async Task UpdateKnownHostsAsync(
        SshSession session, CancellationToken ct)
    {
        Directory.CreateDirectory(KeyDir);
        var knownHostsPath = System.IO.Path.Combine(KeyDir, "known_hosts");

        var (_, stdout, _) = await session.ExecuteAsync(
            $"ssh-keyscan -H -t ed25519 {session.Host} 2>/dev/null | head -1", ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            var existing = File.Exists(knownHostsPath)
                ? File.ReadAllText(knownHostsPath) : "";
            if (!existing.Contains(stdout.Trim()))
                File.AppendAllText(knownHostsPath, stdout.Trim() + Environment.NewLine);
        }
    }

    public static Task UpdateKnownHostsAsync(string host, int port, CancellationToken ct)
        => Task.CompletedTask;

    private static async Task GenerateKeyPairCoreAsync(
        string privPath, string pubPath, CancellationToken ct)
    {
        var sshKeygen = FindSshKeygen()
            ?? throw new ProvisioningException(
                "ssh-keygen not found. Install OpenSSH Client via " +
                "Settings → Apps → Optional Features → Add a Feature → OpenSSH Client.");

        if (File.Exists(privPath)) File.Delete(privPath);
        if (File.Exists(pubPath))  File.Delete(pubPath);

        var proc = new Process
        {
            StartInfo = new ProcessStartInfo(
                sshKeygen,
                $"-t ed25519 -f \"{privPath}\" -N \"\" -q -C \"pidbg@vsix\"")
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            },
            EnableRaisingEvents = true,
        };

        var tcs = new TaskCompletionSource<int>();
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);
        using (ct.Register(() => { try { proc.Kill(); } catch { } tcs.TrySetCanceled(); }))
        {
            proc.Start();
            if (proc.HasExited) tcs.TrySetResult(proc.ExitCode);
            var exitCode = await tcs.Task.ConfigureAwait(false);
            if (exitCode != 0)
            {
                var errOutput = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new ProvisioningException(
                    $"ssh-keygen failed (exit {exitCode}): {errOutput}");
            }
        }

        RestrictFileToCurrentUser(privPath);
    }

    private static void RestrictFileToCurrentUser(string path)
    {
#if NET472
        try
        {
            var fi       = new FileInfo(path);
            var security = fi.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().Name,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
            fi.SetAccessControl(security);
        }
        catch { /* best-effort — non-critical */ }
#endif
    }

    private static string? FindSshKeygen()
    {
        var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var builtin  = System.IO.Path.Combine(system32, "OpenSSH", "ssh-keygen.exe");
        if (File.Exists(builtin)) return builtin;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                            .Split(System.IO.Path.PathSeparator))
        {
            var candidate = System.IO.Path.Combine(dir.Trim(), "ssh-keygen.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string SanitizeHost(string host) =>
        new string(Array.ConvertAll(host.ToCharArray(),
            c => char.IsLetterOrDigit(c) || c == '-' || c == '.' ? c : '_'));
}
