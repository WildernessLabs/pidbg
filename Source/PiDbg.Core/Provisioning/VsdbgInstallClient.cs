using System.Net.Http;
using System.Text;

using Grpc.Core;

using Meadow.Daemon.Contracts.V1;

using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class VsdbgInstallClient
{
    public const string RequiredVsdbgMin = "17.0.0";
    public const string PreferredVsdbg = "latest";

    private const string GetVsDbgShUrl = "https://aka.ms/getvsdbgsh";

    public static bool NeedsInstall(DetectionResult detection)
    {
        if (!detection.Vsdbg.BinaryExists) return true;
        var installed = detection.Vsdbg.Version;
        if (string.IsNullOrEmpty(installed)) return true;
        return IsVersionLessThan(installed, RequiredVsdbgMin);
    }

    public static async Task InstallAsync(
        SshSession session,
        ChannelBase channel,
        string rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        rootFolder = session.ExpandPath(rootFolder);
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var errors = new StringBuilder();

        // 1. Pi-side online install — daemon uses HttpClient + GetVsDbg.sh
        {
            var (succeeded, error) = await TryOnlineInstallAsync(client, progress, ct).ConfigureAwait(false);
            if (succeeded) return;
            if (error != null) errors.Append("- Pi-side: ").AppendLine(error);
        }

        // 2. SSH script install — dev machine downloads GetVsDbg.sh, uploads and runs it on Pi.
        {
            var (succeeded, error) = await TrySshScriptInstallAsync(
                session, rootFolder, progress, ct).ConfigureAwait(false);
            if (succeeded) return;
            if (error != null) errors.Append("- Extension-side: ").AppendLine(error);
        }

        // 3. Bundled offline tarball — for fully air-gapped scenarios
        var tarball = GetEmbeddedTarball();
        if (tarball is null)
        {
            var msg = new StringBuilder();
            msg.AppendLine("vsdbg installation failed across all methods:");
            msg.Append(errors);
            msg.AppendLine("- Offline: No bundled tarball found.");
            msg.AppendLine();
            msg.AppendLine("Common causes:");
            msg.AppendLine("1. Raspberry Pi has no internet (Pi-side failed).");
            msg.AppendLine("2. Dev machine has no internet or aka.ms is blocked (extension-side failed).");
            msg.AppendLine("3. Firewall or proxy is blocking HTTPS traffic.");
            throw new ProvisioningException(msg.ToString());
        }

        progress.Report("Using bundled offline tarball for vsdbg installation...");
        await UploadAndInstallTarballAsync(
            session, client, tarball, GetEmbeddedTarballSha256(), rootFolder, progress, ct)
            .ConfigureAwait(false);
    }

    private static async Task<(bool Success, string? Error)> TryOnlineInstallAsync(
        MeadowDaemonService.MeadowDaemonServiceClient client,
        IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            using var call = client.InstallVsdbg(
                new InstallVsdbgRequest { Version = PreferredVsdbg },
                cancellationToken: ct);

            string? lastError = null;
            while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                var msg = call.ResponseStream.Current;
                if (!string.IsNullOrEmpty(msg.StatusMessage))
                    progress.Report(msg.StatusMessage);
                if (msg.Success)
                    return (true, null);
                if (!string.IsNullOrEmpty(msg.ErrorMessage))
                {
                    lastError = msg.ErrorMessage;
                    progress.Report($"Pi-side install failed: {lastError}. Trying VSIX download...");
                }
            }
            return (false, lastError ?? "Unknown daemon failure");
        }
        catch (RpcException ex)
        {
            var err = ex.Status.Detail;
            progress.Report($"Pi-side install failed: {err}. Trying VSIX download...");
            return (false, err);
        }
    }

    private static async Task<(bool Success, string? Error)> TrySshScriptInstallAsync(
        SshSession session,
        string rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        try
        {
            progress.Report("Downloading GetVsDbg.sh from Microsoft...");
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var script = await http.GetStringAsync(GetVsDbgShUrl).ConfigureAwait(false);

            var tmpDir = $"{rootFolder}/tmp";
            var scriptPath = $"{tmpDir}/GetVsDbg.sh";
            var vsdbgDir = $"{rootFolder}/vsdbg";

            await session.ExecuteAsync($"mkdir -p '{tmpDir}'", ct).ConfigureAwait(false);

            var scriptBytes = Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n"));
            using (var stream = new MemoryStream(scriptBytes))
                await session.UploadFileAsync(stream, scriptPath, null, ct).ConfigureAwait(false);

            progress.Report($"Running GetVsDbg.sh on device (version {PreferredVsdbg})...");
            var cmd = $"chmod +x '{scriptPath}' && " +
                      $"bash '{scriptPath}' -v {PreferredVsdbg} -r linux-arm64 -l '{vsdbgDir}'";
            var (rc, stdout, stderr) = await session
                .ExecuteAsync(cmd, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(stdout)) progress.Report(stdout.Trim());
            if (!string.IsNullOrWhiteSpace(stderr)) progress.Report($"  {stderr.Trim()}");

            await session.ExecuteAsync($"rm -f '{scriptPath}'", ct).ConfigureAwait(false);

            if (rc != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                progress.Report($"GetVsDbg.sh failed (exit {rc}). Trying bundled tarball...");
                return (false, $"Script exited with code {rc}: {err}");
            }

            progress.Report("vsdbg installed successfully.");
            return (true, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress.Report($"SSH script install failed: {ex.Message}. Trying bundled tarball...");
            return (false, ex.Message);
        }
    }

    private static async Task UploadAndInstallTarballAsync(
        SshSession session,
        MeadowDaemonService.MeadowDaemonServiceClient client,
        Stream tarball,
        string sha256,
        string rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var tmpDir = $"{rootFolder}/tmp";
        var remoteTarPath = $"{tmpDir}/vsdbg-linux-arm64.tar.gz";

        await session.ExecuteAsync($"mkdir -p '{tmpDir}'", ct).ConfigureAwait(false);

        progress.Report("Uploading vsdbg tarball...");
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
        var response = await client.UploadVsdbgTarballAsync(
            new UploadVsdbgTarballRequest
            {
                Version = PreferredVsdbg,
                TarballPath = remoteTarPath,
                Sha256 = sha256,
            },
            cancellationToken: ct).ConfigureAwait(false);

        if (!response.Success)
            throw new ProvisioningException($"vsdbg tarball installation failed: {response.ErrorMessage}");

        await session.ExecuteAsync($"rm -f '{remoteTarPath}'", ct).ConfigureAwait(false);
        progress.Report("vsdbg installed successfully.");
    }

    // Tarball lives in the entry-point assembly (VSIX / DebugAdapter).
    private static Stream? GetEmbeddedTarball()
    {
        var asm = ProvisioningResources.Get();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains("vsdbg-linux-arm64.tar.gz",
                StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }

    private static string GetEmbeddedTarballSha256()
    {
        var asm = ProvisioningResources.Get();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains("vsdbg-linux-arm64.tar.gz.sha256",
                StringComparison.OrdinalIgnoreCase));
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
