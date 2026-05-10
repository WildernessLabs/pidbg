namespace Meadow.Daemon;

internal static class DaemonPaths
{
    public static string AppDir(string appRoot, string appName) =>
        Path.Combine(appRoot, appName);

    public static string AppDebugSlot(string appRoot, string appName) =>
        Path.Combine(appRoot, appName, "debug");

    public static string AppVersionsDir(string appRoot, string appName) =>
        Path.Combine(appRoot, appName, "versions");

    public static string AppVersionDir(string appRoot, string appName, string versionLabel) =>
        Path.Combine(appRoot, appName, "versions", versionLabel);

    public static string AppActiveLink(string appRoot, string appName) =>
        Path.Combine(appRoot, appName, "active");

    public static string AppStagingDir(string appRoot, string appName, string deploymentId) =>
        Path.Combine(appRoot, appName, "staging", deploymentId);

    public static string AppManifest(string appRoot, string appName, string slot) =>
        Path.Combine(appRoot, appName, $"{slot}.manifest.json");

    public static string AppLogFile(string logRoot, string appName) =>
        Path.Combine(logRoot, $"{appName}.log");
}
