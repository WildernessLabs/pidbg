using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

using PiDbg.Infrastructure;

namespace PiDbg.Debug;

internal static class PiDbgLaunchProfileWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static Task<(SshConnectionConfig Config, string AppName)?> TryReadAsync(
        string projectPath, CancellationToken ct)
        => Task.Run(() => TryRead(projectPath), ct);

    public static Task WriteAsync(
        string projectPath,
        SshConnectionConfig config,
        string appName,
        CancellationToken ct)
        => Task.Run(() => Write(projectPath, config, appName), ct);

    private static (SshConnectionConfig Config, string AppName)? TryRead(string projectPath)
    {
        var settingsPath = LaunchSettingsPath(projectPath);
        if (!File.Exists(settingsPath)) return null;

        var text = File.ReadAllText(settingsPath);
        if (JsonNode.Parse(text) is not JsonObject root) return null;
        if (root["profiles"] is not JsonObject profiles) return null;

        foreach (var kv in profiles)
        {
            var node = kv.Value;
            // Pi profiles are identified by the presence of PiDbgHost, not commandName.
            var host = node?["PiDbgHost"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(host)) continue;

            return (
                new SshConnectionConfig
                {
                    Host       = host,
                    Port       = int.TryParse(node["PiDbgPort"]?.ToString(), out var p) && p > 0 ? p : 22,
                    User       = NE(node["PiDbgUsername"]?.GetValue<string>(), "pi"),
                    RootFolder = NE(node["PiDbgRootFolder"]?.GetValue<string>(), "~/meadow"),
                    KeyFile    = NullIfEmpty(node["PiDbgPrivateKeyPath"]?.GetValue<string>()),
                    Password   = NullIfEmpty(node["PiDbgPassword"]?.GetValue<string>()),
                },
                node["PiDbgAppName"]?.GetValue<string>() ?? ""
            );
        }

        return null;
    }

    private static void Write(string projectPath, SshConnectionConfig config, string appName)
    {
        var settingsPath = LaunchSettingsPath(projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        JsonObject root;
        if (File.Exists(settingsPath))
        {
            var text = File.ReadAllText(settingsPath);
            root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root["profiles"] is not JsonObject profiles)
        {
            profiles = new JsonObject();
            root["profiles"] = profiles;
        }

        // Remove any pre-existing PiDbg profiles (identified by PiDbgHost) before writing the new one.
        var toRemove = new List<string>();
        foreach (var kv in profiles)
        {
            if (!string.IsNullOrEmpty(profiles[kv.Key]?["PiDbgHost"]?.GetValue<string>()))
                toRemove.Add(kv.Key);
        }
        foreach (var key in toRemove)
            profiles.Remove(key);

        profiles[PiDbgLaunchProfile.BuildProfileName(config.Host)] = new JsonObject
        {
            ["commandName"]         = "Project",
            ["PiDbgHost"]           = config.Host,
            ["PiDbgPort"]           = config.Port,
            ["PiDbgUsername"]       = config.User,
            ["PiDbgRootFolder"]     = config.RootFolder,
            ["PiDbgPassword"]       = config.Password ?? "",
            ["PiDbgPrivateKeyPath"] = config.KeyFile ?? "",
            ["PiDbgAppName"]        = appName,
        };

        File.WriteAllText(settingsPath, root.ToJsonString(WriteOptions));
    }

    private static string LaunchSettingsPath(string projectPath)
        => Path.Combine(Path.GetDirectoryName(projectPath)!, "Properties", "launchSettings.json");

    private static string NE(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
