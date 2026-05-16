using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Meadow.Daemon.Contracts.V1;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class VsdbgInstallClient
{
    public const string RequiredVsdbgMin = "17.0.0";
    public const string PreferredVsdbg   = "17.12.11230";

    public static bool NeedsInstall(DetectionResult detection)
    {
        if (!detection.Vsdbg.BinaryExists) return true;
        var installed = detection.Vsdbg.Version;
        if (string.IsNullOrEmpty(installed)) return true;
        return IsVersionLessThan(installed, RequiredVsdbgMin);
    }

    public static async Task InstallAsync(
        SshSession session,
        Channel channel,
        bool curlAvailable,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);

        if (curlAvailable)
        {
            var succeeded = await TryOnlineInstallAsync(client, progress, ct).ConfigureAwait(false);
            if (succeeded) return;
        }

        progress.Report("Using offline tarball for vsdbg installation...");
        await OfflineTarballInstallAsync(session, client, progress, ct).ConfigureAwait(false);
    }

    private static async Task<bool> TryOnlineInstallAsync(
        MeadowDaemonService.MeadowDaemonServiceClient client,
        IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            using var call = client.InstallVsdbg(
                new InstallVsdbgRequest { Version = PreferredVsdbg },
                cancellationToken: ct);

            // Grpc.Core uses MoveNext/Current — not IAsyncEnumerable
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var msg = call.ResponseStream.Current;
                if (!string.IsNullOrEmpty(msg.StatusMessage))
                    progress.Report(msg.StatusMessage);
                if (msg.Success)
                    return true;
                if (!string.IsNullOrEmpty(msg.ErrorMessage))
                {
                    progress.Report($"Online install failed: {msg.ErrorMessage}. Falling back to tarball.");
                    return false;
                }
            }
            return true;
        }
        catch (RpcException ex)
        {
            progress.Report($"Online install failed: {ex.Status.Detail}. Falling back to tarball.");
            return false;
        }
    }

    private static async Task OfflineTarballInstallAsync(
        SshSession session,
        MeadowDaemonService.MeadowDaemonServiceClient client,
        IProgress<string> progress, CancellationToken ct)
    {
        var tarball = GetEmbeddedTarball();
        if (tarball is null)
            throw new ProvisioningException(
                "vsdbg offline tarball not bundled in this VSIX. " +
                "Ensure the device has internet access for online installation, " +
                "or install the full PiDbg release that bundles the tarball.");

        const string remoteTarPath = "/opt/meadow/tmp/vsdbg-linux-arm64.tar.gz";

        // Ensure tmp dir exists
        await session.ExecuteAsync("mkdir -p /opt/meadow/tmp", ct).ConfigureAwait(false);

        progress.Report("Uploading vsdbg tarball via SFTP...");
        using (tarball)
        {
            var lastMb = -1L;
            await session.UploadFileAsync(
                tarball, remoteTarPath,
                new Progress<long>(bytes =>
                {
                    var mb = bytes / 1024 / 1024 / 5 * 5;
                    if (mb != lastMb) { lastMb = mb; progress.Report($"  {mb} MB uploaded..."); }
                }),
                ct).ConfigureAwait(false);
        }

        progress.Report("Installing vsdbg from tarball...");
        var sha256 = GetEmbeddedTarballSha256();

        var response = await client.UploadVsdbgTarballAsync(
            new UploadVsdbgTarballRequest
            {
                Version     = PreferredVsdbg,
                TarballPath = remoteTarPath,
                Sha256      = sha256,
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!response.Success)
            throw new ProvisioningException(
                $"vsdbg tarball installation failed: {response.ErrorMessage}");

        await session.ExecuteAsync($"rm -f {remoteTarPath}", ct).ConfigureAwait(false);
        progress.Report("vsdbg installed successfully.");
    }

    private static Stream? GetEmbeddedTarball()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.IndexOf("vsdbg-linux-arm64.tar.gz",
                StringComparison.OrdinalIgnoreCase) >= 0);
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static string GetEmbeddedTarballSha256()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.IndexOf("vsdbg-linux-arm64.tar.gz.sha256",
                StringComparison.OrdinalIgnoreCase) >= 0);
        if (name is null) return "";
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd().Trim();
    }

    private static bool IsVersionLessThan(string installed, string required)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(required)) return false;
        if (Version.TryParse(installed, out var v1) && Version.TryParse(required, out var v2))
            return v1 < v2;
        return string.Compare(installed, required, StringComparison.OrdinalIgnoreCase) < 0;
    }
}
