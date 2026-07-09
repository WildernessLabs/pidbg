using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class RuntimeInstaller
{
    internal readonly record struct RequiredFramework(string Name, Version Version);

    // Reads the project's <TargetFramework> (or first entry of <TargetFrameworks>)
    // directly from the .csproj - available immediately, before any SSH connection,
    // provisioning, or publish. This lets the runtime check run and fail fast up
    // front, rather than only being discoverable after a full publish cycle.
    public static RequiredFramework? ReadRequiredFrameworkFromProject(string projectPath)
    {
        if (!File.Exists(projectPath))
            return null;

        try
        {
            var doc = XDocument.Load(projectPath);
            var tfm = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(tfm))
            {
                tfm = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value
                    ?.Split(';')
                    .Select(t => t.Trim())
                    .FirstOrDefault(t => t.Length > 0);
            }

            if (string.IsNullOrWhiteSpace(tfm))
                return null;

            return ParseTfm(tfm!.Trim());
        }
        catch (Exception ex) when (ex is System.Xml.XmlException || ex is IOException || ex is UnauthorizedAccessException)
        {
            return null; // Don't block on a parsing issue - fail open.
        }
    }

    // Converts an SDK-style TFM (net10.0, net8.0-windows, netcoreapp3.1) into the
    // shared-runtime version it maps to. Returns null for TFMs with no shared-runtime
    // concept (e.g. classic .NET Framework "net472") - nothing to check there.
    private static RequiredFramework? ParseTfm(string tfm)
    {
        var dash = tfm.IndexOf('-');
        var core = dash >= 0 ? tfm.Substring(0, dash) : tfm;

        string? versionText = null;
        if (core.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            versionText = core.Substring("netcoreapp".Length);
        else if (core.StartsWith("net", StringComparison.OrdinalIgnoreCase) && core.Contains("."))
            versionText = core.Substring("net".Length);

        if (versionText is null || !Version.TryParse(versionText, out var version))
            return null;

        return new RequiredFramework("Microsoft.NETCore.App", version);
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
