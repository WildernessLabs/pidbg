using System.IO;
using System.Reflection;
using System.Text;

using Grpc.Core;

using Meadow.Daemon.Contracts.V1;

using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

public enum DaemonInstallAction { None, Install, Upgrade, Reinstall }

internal static class DaemonInstaller
{
    // Populated at VSIX build time by a T4 or source generator stamping the bundled binary.
    // Placeholder values are safe: without RequiredSha256, the SHA check is skipped.
    public const string RequiredVersion = "1.0.13";
    public const string RequiredSha256 = "";

    public static DaemonInstallAction DetermineAction(DetectionResult detection)
    {
        if (!detection.Daemon.BinaryExists) return DaemonInstallAction.Install;
        if (detection.Daemon.BinaryVersion == "") return DaemonInstallAction.Reinstall;

        if (!string.IsNullOrEmpty(RequiredSha256)
            && !string.IsNullOrEmpty(detection.Daemon.BinarySha256)
            && detection.Daemon.BinarySha256 != RequiredSha256)
            return DaemonInstallAction.Reinstall;

        if (IsVersionLessThan(detection.Daemon.BinaryVersion, RequiredVersion))
            return DaemonInstallAction.Upgrade;

        return DaemonInstallAction.None;
    }

    public static async Task InstallAsync(
        SshSession session,
        DaemonInstallAction action,
        string rootFolder,
        string dotnetRoot,
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (action == DaemonInstallAction.None) return;

        progress.Report($"Installing daemon ({action})...");

        var binaryStream = GetEmbeddedBinary();
        if (binaryStream is null)
            throw new ProvisioningException(
                "Daemon binary not bundled in this VSIX. " +
                "Please install the official release of the PiDbg extension.");

        // Expand ~ to absolute path — systemd and single-quoted SSH args don't expand it.
        var absRoot = session.ExpandPath(rootFolder);
        var binDir = $"{absRoot}/bin";
        var daemonBin = $"{binDir}/meadow-daemon";

        // Ensure bin directory exists
        await session.ExecuteAsync($"mkdir -p '{binDir}'", ct).ConfigureAwait(false);

        if (action == DaemonInstallAction.Upgrade)
        {
            progress.Report("Backing up existing daemon binary...");
            await session.ExecuteAsync(
                $"cp '{daemonBin}' '{daemonBin}.bak' 2>/dev/null || true",
                ct).ConfigureAwait(false);
        }

        var sizeMb = GetBinarySize() / 1024 / 1024;
        progress.Report($"Uploading meadow-daemon ({sizeMb} MB)...");
        using (binaryStream)
        {
            var lastMb = -1L;
            await session.UploadFileAsync(
                binaryStream,
                $"{daemonBin}.new",
                new Progress<long>(bytes =>
                {
                    var mb = bytes / 1024 / 1024 / 5 * 5;
                    if (mb != lastMb) { lastMb = mb; progress.Report($"  {mb} MB uploaded..."); }
                }),
                ct).ConfigureAwait(false);
        }

        var (rc, _, err) = await session.ExecuteAsync(
            $"chmod 755 '{daemonBin}.new' && mv '{daemonBin}.new' '{daemonBin}'",
            ct).ConfigureAwait(false);
        if (rc != 0)
            throw new ProvisioningException($"Failed to install daemon binary: {err}");

        progress.Report("Installing systemd service...");
        var serviceContent = RenderServiceTemplate(absRoot, dotnetRoot);
        var serviceDir = session.ExpandPath("~/.config/systemd/user");
        await session.ExecuteAsync($"mkdir -p '{serviceDir}'", ct).ConfigureAwait(false);
        await UploadTextAsync(session, serviceContent,
            $"{serviceDir}/meadow-daemon.service", ct)
            .ConfigureAwait(false);

        progress.Report("Enabling and starting service...");
        var startCmd = action == DaemonInstallAction.Upgrade
            ? "systemctl --user daemon-reload && systemctl --user restart meadow-daemon"
            : "systemctl --user daemon-reload && " +
              "systemctl --user enable meadow-daemon && " +
              "systemctl --user start meadow-daemon";

        (rc, _, err) = await session.ExecuteAsync(startCmd, ct).ConfigureAwait(false);
        if (rc != 0)
            throw new ProvisioningException($"Failed to start service: {err}");

        progress.Report("Daemon installed and running.");
    }

    public static async Task UpdateServiceAsync(
        SshSession session,
        string rootFolder,
        string dotnetRoot,
        IProgress<string> progress,
        CancellationToken ct)
    {
        progress.Report("Updating systemd service with new DOTNET_ROOT...");
        var absRoot = session.ExpandPath(rootFolder);
        var serviceContent = RenderServiceTemplate(absRoot, dotnetRoot);
        var serviceDir = session.ExpandPath("~/.config/systemd/user");
        await session.ExecuteAsync($"mkdir -p '{serviceDir}'", ct).ConfigureAwait(false);
        await UploadTextAsync(session, serviceContent,
            $"{serviceDir}/meadow-daemon.service", ct).ConfigureAwait(false);

        var (rc, _, err) = await session.ExecuteAsync(
            "systemctl --user daemon-reload && systemctl --user restart meadow-daemon",
            ct).ConfigureAwait(false);
        if (rc != 0)
            throw new ProvisioningException($"Failed to restart service after update: {err}");

        progress.Report("Service updated and restarted.");
    }

    public static async Task<bool> WaitForHealthAsync(
        Channel channel, TimeSpan timeout, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await client.PingAsync(new PingRequest(), cancellationToken: ct)
                             .ConfigureAwait(false);
                return true;
            }
            catch (Exception)
            {
                /* daemon not ready yet or tunnel not established */
            }
            await Task.Delay(2000, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static string RenderServiceTemplate(string rootFolder, string dotnetRoot)
    {
        var template = LoadEmbeddedText("meadow-daemon.service.template");
        return template
            .Replace("@@DAEMON_BIN@@", $"{rootFolder}/bin/meadow-daemon")
            .Replace("@@INSTALL_DIR@@", rootFolder)
            .Replace("@@DOTNET_ROOT@@", dotnetRoot)
            .Replace("@@EXTRA_ARGS@@", "")
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");
    }

    private static Stream? GetEmbeddedBinary()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("meadow-daemon", StringComparison.Ordinal));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static long GetBinarySize()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("meadow-daemon", StringComparison.Ordinal));
        if (name is null) return 0;
        using var stream = asm.GetManifestResourceStream(name)!;
        return stream.Length;
    }

    private static string LoadEmbeddedText(string resourceSuffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static async Task UploadTextAsync(
        SshSession session, string content, string remotePath, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        await session.UploadFileAsync(stream, remotePath, null, ct).ConfigureAwait(false);
    }

    private static bool IsVersionLessThan(string installed, string required)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(required)) return false;
        // Strip +commithash suffix appended by .NET SDK to AssemblyInformationalVersion.
        var cleanInstalled = installed.Split('+')[0].Trim();
        var cleanRequired = required.Split('+')[0].Trim();
        if (Version.TryParse(cleanInstalled, out var v1) && Version.TryParse(cleanRequired, out var v2))
            return v1 < v2;
        return string.Compare(cleanInstalled, cleanRequired, StringComparison.OrdinalIgnoreCase) < 0;
    }
}
