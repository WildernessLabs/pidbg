using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

// Controls the staging area and hard-link delta transfer.
internal class StagingController
{
    private readonly DaemonOptions _options;
    private readonly ILogger<StagingController> _logger;

    public StagingController(IOptions<DaemonOptions> options, ILogger<StagingController> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates a fresh staging directory. If one already exists (crash recovery), delete it first.
    /// </summary>
    public string CreateStaging(string appName)
    {
        var path = DaemonPaths.AppStagingDir(_options, appName);
        if (Directory.Exists(path))
        {
            _logger.LogWarning("Stale staging directory found for {App}; cleaning up", appName);
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Deletes the staging directory. Safe to call if it doesn't exist.
    /// </summary>
    public void CleanStaging(string appName)
    {
        var path = DaemonPaths.AppStagingDir(_options, appName);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// <summary>
    /// Hard-links files from `sourceDir` into staging that match entries in `manifest`
    /// where the SHA-256 in the manifest matches the file in sourceDir.
    /// Returns the set of relative file paths that were linked (already present, no upload needed).
    /// </summary>
    public async Task<HashSet<string>> HardLinkUnchangedFilesAsync(
        string appName,
        string sourceDir,
        DeploymentManifest newManifest,
        CancellationToken ct)
    {
        var stagingDir = DaemonPaths.AppStagingDir(_options, appName);
        var linked = new HashSet<string>(StringComparer.Ordinal);

        // Build a lookup of relative path → SHA-256 from the source directory's manifest
        var sourceMf = await TryReadManifestAsync(sourceDir, ct);
        if (sourceMf is null) return linked;
        
        var sourceMap = sourceMf.Files.ToDictionary(f => f.Path, f => f.Sha256);

        foreach (var entry in newManifest.Files)
        {
            if (!sourceMap.TryGetValue(entry.Path, out var sourceSha256)) continue;
            if (sourceSha256 != entry.Sha256) continue;  // content changed — don't link

            var sourcePath  = Path.Combine(sourceDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            var stagingPath = Path.Combine(stagingDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(sourcePath)) continue;

            try
            {
                var dir = Path.GetDirectoryName(stagingPath);
                if (dir != null) Directory.CreateDirectory(dir);

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    // Hard link: both paths point to the same inode — zero disk copy
                    var rc = Mono.Unix.Native.Syscall.link(sourcePath, stagingPath);
                    if (rc == 0)
                        linked.Add(entry.Path);
                    else
                        _logger.LogDebug("Hard link failed for {File} (errno={Errno}); will upload", entry.Path, Mono.Unix.Native.Stdlib.GetLastError());
                }
                else
                {
                    // Non-linux: fallback to copy if we wanted to support it, 
                    // but per spec we just return empty/failed and let it upload.
                    _logger.LogDebug("Hard link not supported on this platform for {File}; will upload", entry.Path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to link {File}; will upload", entry.Path);
            }
        }

        _logger.LogInformation("Delta: {Linked}/{Total} files hard-linked",
            linked.Count, newManifest.Files.Count);
        return linked;
    }

    private static async Task<DeploymentManifest?> TryReadManifestAsync(
        string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var f = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync(f, 
                Meadow.Daemon.Models.DaemonJsonContext.Default.DeploymentManifest,
                cancellationToken: ct);
        }
        catch { return null; }
    }
}
