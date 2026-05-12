using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
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

    public async Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Downloading GetVsDbg.sh...");
        var scriptPath = Path.Combine(DaemonPaths.TempDir(), "GetVsDbg.sh");

        using var http = new HttpClient();
        var script = await http.GetStringAsync("https://aka.ms/getvsdbgsh", ct);
        
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        await File.WriteAllTextAsync(scriptPath, script, ct);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Mono.Unix.Native.Syscall.chmod(scriptPath, Mono.Unix.Native.FilePermissions.S_IRWXU);
        }

        var vsdbgDir = DaemonPaths.VsdbgDir(_options);
        var args     = $"-Version {version} -RuntimeID linux-arm64 -InstallPath {vsdbgDir}";

        progress.Report($"Running GetVsDbg.sh {args}...");
        
        var startInfo = new ProcessStartInfo("bash", $"{scriptPath} {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("Vsdbg install via bash script is only supported on Linux.");
        }

        var process = new Process { StartInfo = startInfo };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) progress.Report(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data != null) progress.Report($"ERR: {e.Data}"); };
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new VsdbgInstallException($"GetVsDbg.sh failed with exit code {process.ExitCode}");

        progress.Report("vsdbg installed successfully.");
    }

    public async Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256,
        IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Receiving vsdbg tarball...");
        var tarPath = Path.Combine(DaemonPaths.TempDir(), "vsdbg.tar.gz");

        Directory.CreateDirectory(Path.GetDirectoryName(tarPath)!);
        await using (var fs = File.Create(tarPath))
            await tarball.CopyToAsync(fs, ct);

        // Verify integrity
        progress.Report("Verifying tarball integrity...");
        var actualSha256 = await ComputeSha256Async(tarPath, ct);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tarPath);
            throw new VsdbgInstallException($"Tarball SHA-256 mismatch. Expected: {expectedSha256}, got: {actualSha256}");
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
            Mono.Unix.Native.Syscall.chmod(binPath,
                Mono.Unix.Native.FilePermissions.S_IRWXU | 
                Mono.Unix.Native.FilePermissions.S_IRGRP | 
                Mono.Unix.Native.FilePermissions.S_IXGRP);
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
