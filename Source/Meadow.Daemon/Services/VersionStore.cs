using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

namespace Meadow.Daemon.Services;

// Manages the versioned directory tree under {AppRoot}/{appName}/versions/ and
// the `active` symlink that points to the current production slot.
internal class VersionStore
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VersionStore> _logger;

    public VersionStore(IOptions<DaemonOptions> options, ILogger<VersionStore> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Returns all version IDs for the app, sorted chronologically (ULID lexicographic).
    /// </summary>
    public IReadOnlyList<string> ListVersions(string appName)
    {
        var dir = DaemonPaths.AppVersionsDir(_options, appName);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .OrderBy(id => id)   // ULID sorts chronologically
                        .ToList();
    }

    /// <summary>
    /// Returns the ULID that `active` symlink points to, or null if no active version.
    /// </summary>
    public string? GetActiveVersion(string appName)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null; // Symlink logic is Linux-only in this system

        var link = DaemonPaths.AppActiveSymlink(_options, appName);
        if (!File.Exists(link) && !Directory.Exists(link)) return null;

        try
        {
            // Mono.Unix: Syscall.readlink to get symlink target
            var result = Mono.Unix.UnixPath.ReadLink(link);
            return Path.GetFileName(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read active symlink for {App}", appName);
            return null;
        }
    }

    /// <summary>
    /// Creates the version directory. Caller fills it with files, then calls SetActiveVersion.
    /// </summary>
    public string CreateVersionDirectory(string appName, string versionId)
    {
        DaemonPaths.SanitizeName(appName);
        DaemonPaths.SanitizeName(versionId);
        var path = DaemonPaths.AppVersionDir(_options, appName, versionId);
        Directory.CreateDirectory(path);
        _logger.LogDebug("Created version directory {Path}", path);
        return path;
    }

    /// <summary>
    /// Atomically swaps the `active` symlink to point to versionId.
    /// Pattern: create `active.new` symlink → rename `active.new` → `active` (atomic).
    /// </summary>
    public void SetActiveVersion(string appName, string versionId)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            throw new PlatformNotSupportedException("SetActiveVersion is only supported on Linux.");

        DaemonPaths.SanitizeName(appName);
        DaemonPaths.SanitizeName(versionId);

        // Target must be relative to allow moving the root tree
        var target      = Path.Combine("versions", versionId); 
        var activeLink  = DaemonPaths.AppActiveSymlink(_options, appName);
        var newLink     = activeLink + ".new";

        // Remove stale .new if it exists (crash recovery)
        if (File.Exists(newLink) || Directory.Exists(newLink))
        {
            if (Directory.Exists(newLink)) Directory.Delete(newLink, recursive: true);
            else File.Delete(newLink);
        }

        // Create the new symlink pointing to versions/{versionId}
        var rc = Mono.Unix.Native.Syscall.symlink(target, newLink);
        if (rc != 0) throw new IOException(
            $"symlink({target}, {newLink}) failed: {Mono.Unix.Native.Stdlib.GetLastError()}");

        // Atomic rename: .new → active
        rc = Mono.Unix.Native.Syscall.rename(newLink, activeLink);
        if (rc != 0) throw new IOException(
            $"rename({newLink}, {activeLink}) failed: {Mono.Unix.Native.Stdlib.GetLastError()}");

        _logger.LogInformation("Set active version for {App} → {Version}", appName, versionId);
    }

    public void DeleteVersion(string appName, string versionId)
    {
        if (GetActiveVersion(appName) == versionId)
            throw new InvalidOperationException(
                $"Cannot delete active version '{versionId}' of app '{appName}'");

        var path = DaemonPaths.AppVersionDir(_options, appName, versionId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            _logger.LogInformation("Deleted version {Version} of {App}", versionId, appName);
        }
    }

    /// <summary>
    /// Rolls back to the version immediately preceding the current active version.
    /// </summary>
    public RollbackResult Rollback(string appName)
    {
        var versions = ListVersions(appName);  // sorted chronologically
        var current  = GetActiveVersion(appName);
        if (current is null) return new RollbackResult(false, null, "No active version");

        string? previous = null;
        foreach (var v in versions)
        {
            if (v == current) break;
            previous = v;
        }

        if (previous is null)
            return new RollbackResult(false, null, "No previous version to roll back to");

        SetActiveVersion(appName, previous);
        return new RollbackResult(true, previous, null);
    }
}

public record RollbackResult(bool Success, string? Version, string? Error);
