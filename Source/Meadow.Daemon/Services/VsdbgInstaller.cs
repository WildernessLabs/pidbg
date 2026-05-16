using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

internal class VsdbgInstaller : IVsdbgInstaller
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VsdbgInstaller> _logger;

    public VsdbgInstaller(IOptions<DaemonOptions> options, ILogger<VsdbgInstaller> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string? GetInstalledVersion()
    {
        var vf = DaemonPaths.VsdbgVersionFile(_options);
        if (!File.Exists(vf)) return null;
        try { return File.ReadAllText(vf).Trim(); }
        catch { return null; }
    }

    public Task<bool> IsInstalledAsync(string requiredVersion)
    {
        var version = GetInstalledVersion();
        if (version is null) return Task.FromResult(false);
        if (!File.Exists(DaemonPaths.VsdbgBinPath(_options))) return Task.FromResult(false);

        return Task.FromResult(VersionSatisfies(version, requiredVersion));
    }

    [SupportedOSPlatform("linux")]
    public async Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct)
    {
        var scriptPath = Path.Combine(DaemonPaths.TempDir(), "GetVsDbg.sh");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);

        // 1. Try downloading GetVsDbg.sh
        var downloaded = await TryDownloadScriptAsync(scriptPath, progress, ct);
        if (!downloaded)
            throw new VsdbgInstallException("Failed to download GetVsDbg.sh via HttpClient, wget, or curl.");

        File.SetUnixFileMode(scriptPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var vsdbgDir = DaemonPaths.VsdbgDir(_options);

        progress.Report($"Running GetVsDbg.sh -v {version} -r linux-arm64 -l {vsdbgDir}...");

        var startInfo = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add(version);
        startInfo.ArgumentList.Add("-r");
        startInfo.ArgumentList.Add("linux-arm64");
        startInfo.ArgumentList.Add("-l");
        startInfo.ArgumentList.Add(vsdbgDir);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("Vsdbg install via bash script is only supported on Linux.");
        }

        var process = new Process { StartInfo = startInfo };
        var outputBuilder = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) { progress.Report(e.Data); outputBuilder.AppendLine(e.Data); } };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) { progress.Report("ERR: " + e.Data); outputBuilder.Append("ERR: ").AppendLine(e.Data); } };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var log = outputBuilder.ToString();
            _logger.LogError("GetVsDbg.sh failed with code {Code}. Log: {Log}", process.ExitCode, log);
            throw new VsdbgInstallException($"GetVsDbg.sh failed with exit code {process.ExitCode}");
        }

        progress.Report("vsdbg installed successfully.");
    }

    private async Task<bool> TryDownloadScriptAsync(string path, IProgress<string> progress, CancellationToken ct)
    {
        const string url = "https://aka.ms/getvsdbgsh";

        // Attempt 1: HttpClient
        try
        {
            progress.Report("Downloading GetVsDbg.sh (HttpClient)...");
            using var http = new HttpClient();
            var script = await http.GetStringAsync(url, ct);
            await File.WriteAllTextAsync(path, script, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HttpClient download of GetVsDbg.sh failed");
        }

        // Attempt 2: wget
        try
        {
            progress.Report("Downloading GetVsDbg.sh (wget)...");
            var psi = new ProcessStartInfo("wget", $"-q \"{url}\" -O \"{path}\"") { UseShellExecute = false };
            var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && File.Exists(path)) return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "wget download of GetVsDbg.sh failed");
        }

        // Attempt 3: curl
        try
        {
            progress.Report("Downloading GetVsDbg.sh (curl)...");
            var psi = new ProcessStartInfo("curl", $"-fSL \"{url}\" -o \"{path}\"") { UseShellExecute = false };
            var p = Process.Start(psi);
            if (p != null)
            {
                await p.WaitForExitAsync(ct);
                if (p.ExitCode == 0 && File.Exists(path)) return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "curl download of GetVsDbg.sh failed");
        }

        return false;
    }

    [SupportedOSPlatform("linux")]
    public async Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256,
        IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Receiving vsdbg tarball...");
        var tarPath = Path.Combine(DaemonPaths.TempDir(), "vsdbg.tar.gz");

        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        await using (var fs = File.Create(tarPath))
            await tarball.CopyToAsync(fs, ct);

        // Verify integrity (skip if no expected hash provided)
        if (!string.IsNullOrEmpty(expectedSha256))
        {
            progress.Report("Verifying tarball integrity...");
            var actualSha256 = await ComputeSha256Async(tarPath, ct);
            if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(tarPath);
                throw new VsdbgInstallException($"Tarball SHA-256 mismatch. Expected: {expectedSha256}, got: {actualSha256}");
            }
        }

        // Extract
        var vsdbgDir = DaemonPaths.VsdbgDir(_options);
        progress.Report($"Extracting to {vsdbgDir}...");
        Directory.CreateDirectory(vsdbgDir);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            File.Delete(tarPath);
            throw new PlatformNotSupportedException("Vsdbg extraction via tar is only supported on Linux.");
        }

        var extract = new Process
        {
            StartInfo = new ProcessStartInfo("tar", $"-xzf {tarPath} -C {vsdbgDir}")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            }
        };

        extract.Start();
        await extract.WaitForExitAsync(ct);

        if (extract.ExitCode != 0)
        {
            var err = await extract.StandardError.ReadToEndAsync(ct);
            throw new VsdbgInstallException($"tar extraction failed: {err}");
        }

        // Set execute bit
        var binPath = DaemonPaths.VsdbgBinPath(_options);
        if (File.Exists(binPath))
        {
            File.SetUnixFileMode(binPath,
                UnixFileMode.UserRead  | UnixFileMode.UserWrite  | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }

        File.Delete(tarPath);
        progress.Report("vsdbg installed from offline tarball.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VersionSatisfies(string installed, string required)
    {
        if (string.Equals(required, "latest", StringComparison.OrdinalIgnoreCase)) return true;
        if (required.EndsWith(".x", StringComparison.Ordinal))
        {
            var prefix = required[..^2];
            return installed.StartsWith(prefix, StringComparison.Ordinal);
        }
        return string.Compare(installed, required, StringComparison.Ordinal) >= 0;
    }
}
