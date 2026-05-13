using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PiDbg.Infrastructure;
using Props = PiDbg.Infrastructure.ProjectPropertyReader;

namespace PiDbg.ProjectSystem;

// Reads and writes PiDbg MSBuild properties for the startup project.
// All IVsBuildPropertyStorage COM calls must happen on the UI thread.
internal sealed class ProjectPropertyReader
{
    private readonly AsyncPackage _package;

    public ProjectPropertyReader(AsyncPackage package)
    {
        _package = package;
    }

    public async Task<SshConnectionConfig?> GetConnectionConfigAsync(CancellationToken ct)
    {
        var storage = await GetBuildPropertyStorageAsync(ct).ConfigureAwait(false);
        if (storage is null) return null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        // Connection props may be in .csproj.user (PST_USER_FILE) or .csproj (PST_PROJECT_FILE).
        // Try user file first; fall back to project file for legacy configs.
        var host = ReadBestEffort(storage, Props.PropHost);
        if (string.IsNullOrWhiteSpace(host)) return null;

        return new SshConnectionConfig
        {
            Host    = host,
            User    = NonEmpty(ReadBestEffort(storage, Props.PropUsername), "pi"),
            Port    = int.TryParse(ReadBestEffort(storage, Props.PropPort), out var p) ? p : 22,
            KeyFile = NullIfEmpty(ReadBestEffort(storage, Props.PropPrivateKeyPath)),
        };
    }

    public async Task<string> GetAppNameAsync(CancellationToken ct)
    {
        var storage = await GetBuildPropertyStorageAsync(ct).ConfigureAwait(false);
        if (storage is null) return "MyApp";

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        storage.GetPropertyValue(Props.PropAppName, null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var appName);
        if (!string.IsNullOrEmpty(appName)) return appName;

        storage.GetPropertyValue("AssemblyName", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var assemblyName);
        return string.IsNullOrEmpty(assemblyName) ? "MyApp" : assemblyName;
    }

    public async Task SetConnectionConfigAsync(SshConnectionConfig config, CancellationToken ct)
    {
        var storage = await GetBuildPropertyStorageAsync(ct).ConfigureAwait(false);
        if (storage is null) return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var userFile = (uint)_PersistStorageType.PST_USER_FILE;
        storage.SetPropertyValue(Props.PropHost,           null, userFile, config.Host);
        storage.SetPropertyValue(Props.PropPort,           null, userFile, config.Port.ToString());
        storage.SetPropertyValue(Props.PropUsername,       null, userFile, config.User);
        storage.SetPropertyValue(Props.PropPrivateKeyPath, null, userFile, config.KeyFile ?? "");
    }

    public async Task SetAppNameAsync(string appName, CancellationToken ct)
    {
        var storage = await GetBuildPropertyStorageAsync(ct).ConfigureAwait(false);
        if (storage is null) return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        storage.SetPropertyValue(Props.PropAppName, null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, appName);
    }

    public async Task EnsurePiDbgCapabilityAsync(CancellationToken ct)
    {
        var projectPath = await GetProjectPathAsync(ct).ConfigureAwait(false);
        if (projectPath is null || !File.Exists(projectPath)) return;

        var doc = XDocument.Load(projectPath);
        XNamespace ns = doc.Root?.Name.Namespace ?? XNamespace.None;

        var alreadyPresent = doc.Descendants(ns + "ProjectCapability")
            .Any(e => (string?)e.Attribute("Include") == "PiDbg");

        if (alreadyPresent) return;

        doc.Root!.Add(new XElement(ns + "ItemGroup",
            new XElement(ns + "ProjectCapability",
                new XAttribute("Include", "PiDbg"))));

        doc.Save(projectPath);

        // Reload so VS picks up the new capability and activates PiDbgDebugLaunchProvider.
        var solution = await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false) as IVsSolution;
        var solution4 = solution as IVsSolution4;
        if (solution4 is null) return;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var hier = GetStartupHierarchy(solution);
        if (hier is null) return;

        ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(hier, out var projectGuid));
        solution4.ReloadProject(ref projectGuid);
    }

    public async Task<string?> GetProjectPathAsync(CancellationToken ct)
    {
        var solution = await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false) as IVsSolution;
        if (solution is null) return null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var hier = GetStartupHierarchy(solution);
        if (hier is null) return null;

        hier.GetCanonicalName((uint)VSConstants.VSITEMID.Root, out var path);
        return path;
    }

    private async Task<IVsBuildPropertyStorage?> GetBuildPropertyStorageAsync(CancellationToken ct)
    {
        var solution = await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false) as IVsSolution;
        if (solution is null) return null;

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var hier = GetStartupHierarchy(solution);
        return hier as IVsBuildPropertyStorage;
    }

    private static IVsHierarchy? GetStartupHierarchy(IVsSolution solution)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var guid = Guid.Empty;
        ErrorHandler.ThrowOnFailure(
            solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref guid, out var enumerator));

        if (enumerator is null) return null;

        var items = new IVsHierarchy[1];
        enumerator.Next(1, items, out var fetched);
        return fetched > 0 ? items[0] : null;
    }

    // Tries user file first, then project file.
    private static string ReadBestEffort(IVsBuildPropertyStorage storage, string name)
    {
        storage.GetPropertyValue(name, null, (uint)_PersistStorageType.PST_USER_FILE, out var val);
        if (!string.IsNullOrEmpty(val)) return val;
        storage.GetPropertyValue(name, null, (uint)_PersistStorageType.PST_PROJECT_FILE, out val);
        return val ?? "";
    }

    private static string NonEmpty(string? value, string fallback)
        => string.IsNullOrEmpty(value) ? fallback : value;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
