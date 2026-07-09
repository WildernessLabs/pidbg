using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class RuntimeInstaller
{
    internal readonly record struct RequiredFramework(string Name, Version Version);

    // Reads the app's own runtimeconfig.json (produced by `dotnet publish` on the dev
    // machine) to find the specific .NET runtime it requires. Mirrors the JSON shape
    // parsed server-side by Meadow.Daemon's ProcessManager.CheckRuntimeCompatibility.
    public static RequiredFramework? ReadRequiredFramework(string publishDir, string appName)
    {
        var runtimeConfigPath = Path.Combine(publishDir, $"{appName}.runtimeconfig.json");
        if (!File.Exists(runtimeConfigPath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfigPath));
            if (!doc.RootElement.TryGetProperty("runtimeOptions", out var runtimeOptions))
                return null;
            if (!runtimeOptions.TryGetProperty("framework", out var framework))
                return null;
            if (!framework.TryGetProperty("name", out var nameEl) ||
                !framework.TryGetProperty("version", out var versionEl))
                return null;

            var name = nameEl.GetString();
            var versionText = versionEl.GetString();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(versionText))
                return null;
            if (!Version.TryParse(versionText, out var version))
                return null;

            return new RequiredFramework(name!, version);
        }
        catch (JsonException)
        {
            return null; // Malformed/unexpected shape - don't block on a parsing issue.
        }
    }

    // Checks whether the device already has an installed shared runtime satisfying
    // the requirement (same major version, >= required version).
    public static async Task<(bool Satisfied, string InstalledText)> IsSatisfiedAsync(
        SshSession session, RequiredFramework required, CancellationToken ct)
    {
        const string script =
            "ROOT=$(dirname \"$(command -v dotnet)\" 2>/dev/null); " +
            "[ -z \"$ROOT\" ] && ROOT=\"$HOME/.dotnet\"; " +
            "ls \"$ROOT/shared/{0}/\" 2>/dev/null";

        var cmd = string.Format(System.Globalization.CultureInfo.InvariantCulture, script, required.Name);
        var (_, stdout, _) = await session.ExecuteAsync(cmd, ct).ConfigureAwait(false);

        var installed = stdout
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(line => Version.TryParse(line, out var v) ? v : null)
            .Where(v => v != null)
            .Cast<Version>()
            .ToList();

        var satisfied = installed.Any(v => v.Major == required.Version.Major && v >= required.Version);
        var installedText = installed.Count > 0
            ? string.Join(", ", installed.OrderByDescending(v => v))
            : "none";

        return (satisfied, installedText);
    }

    // Installs the specific runtime channel required, via the same dotnet-install.sh
    // mechanism DotnetInstaller uses for the "nothing installed at all" case, but
    // targeted at the app's required channel instead of --version latest.
    public static async Task InstallAsync(
        SshSession session, RequiredFramework required,
        IProgress<string> progress, CancellationToken ct)
    {
        progress.Report($"Downloading and running dotnet-install.sh (channel {required.Version.Major}.{required.Version.Minor})...");

        var cmd = "wget --inet4-only https://dot.net/v1/dotnet-install.sh -O - " +
                  $"| bash /dev/stdin --channel {required.Version.Major}.{required.Version.Minor} --runtime dotnet 2>&1";

        var (rc, stdout, _) = await session.ExecuteAsync(
            cmd, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        if (rc != 0)
            throw new ProvisioningException(
                $".NET runtime install script failed (exit {rc}):\n{stdout.Trim()}");

        progress.Report("Updating ~/.bashrc with DOTNET_ROOT...");
        var bashrcCmd =
            "grep -qF 'DOTNET_ROOT' ~/.bashrc || " +
            "echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc && " +
            "grep -qF '$HOME/.dotnet' ~/.bashrc || " +
            "echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc";
        await session.ExecuteAsync(bashrcCmd, ct).ConfigureAwait(false);

        progress.Report($".NET {required.Version.Major}.{required.Version.Minor} runtime installed successfully.");
    }
}
