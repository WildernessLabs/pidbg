using Meadow.Daemon.Services;

namespace Meadow.Daemon;

// All methods are static and pure — given the same options they return the same path.
// Callers do not mutate these paths; they call EnsureDirectories once at startup.
public static class DaemonPaths
{
    // Daemon binary
    public static string BinDir(DaemonOptions o)        => Path.Combine(o.InstallRoot, "bin");
    public static string BinPath(DaemonOptions o)       => Path.Combine(BinDir(o), "meadow-daemon");
    public static string BinBackupPath(DaemonOptions o) => Path.Combine(BinDir(o), "meadow-daemon.bak");

    // App trees
    public static string AppsDir(DaemonOptions o)       => o.AppRoot;
    public static string AppDir(DaemonOptions o, string appName)
        => Path.Combine(o.AppRoot, SanitizeName(appName));
    public static string AppDebugDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "debug");
    public static string AppStagingDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "staging");
    public static string AppVersionsDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "versions");
    public static string AppVersionDir(DaemonOptions o, string appName, string versionId)
        => Path.Combine(AppVersionsDir(o, appName), SanitizeName(versionId));
    public static string AppActiveSymlink(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "active");
    public static string AppLocksDir(DaemonOptions o)
        => Path.Combine(o.AppRoot, ".locks");
    public static string AppManifestPath(DaemonOptions o, string appName, string versionId)
        => Path.Combine(AppVersionDir(o, appName, versionId), "manifest.json");

    // vsdbg
    public static string VsdbgDir(DaemonOptions o)       => o.VsdbgRoot;
    public static string VsdbgBinPath(DaemonOptions o)   => Path.Combine(o.VsdbgRoot, "vsdbg-ui");
    public static string VsdbgVersionFile(DaemonOptions o) => Path.Combine(o.VsdbgRoot, ".version");

    // State
    public static string StateDir(DaemonOptions o)       => o.StateRoot;
    public static string AppsStatePath(DaemonOptions o)  => Path.Combine(o.StateRoot, "apps.json");
    public static string SessionsStatePath(DaemonOptions o) => Path.Combine(o.StateRoot, "sessions.json");

    // Logs
    public static string LogDir(DaemonOptions o) => o.LogRoot;

    // Temp
    public static string TempDir() => Path.Combine(Path.GetTempPath(), "meadow-daemon");

    // Creates all base directories that must exist at startup.
    // Call once from IHostedService.StartAsync or Program.cs.
    public static void EnsureDirectories(DaemonOptions o)
    {
        Directory.CreateDirectory(BinDir(o));
        Directory.CreateDirectory(AppsDir(o));
        Directory.CreateDirectory(AppLocksDir(o));
        Directory.CreateDirectory(VsdbgDir(o));
        Directory.CreateDirectory(StateDir(o));
        Directory.CreateDirectory(LogDir(o));
        Directory.CreateDirectory(TempDir());
    }

    // Prevents directory traversal: app names and version IDs must be safe path components.
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty", nameof(name));
        // Allow alphanumeric, hyphen, underscore, dot — reject everything else
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+$"))
            throw new ArgumentException($"Invalid name '{name}': only [a-zA-Z0-9._-] allowed", nameof(name));
        // Prevent traversal
        if (name.Contains("..") || name.StartsWith('.'))
            throw new ArgumentException($"Invalid name '{name}': traversal not allowed", nameof(name));
        return name;
    }
}
