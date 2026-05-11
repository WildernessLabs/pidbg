using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using PiDbg.Infrastructure;

namespace PiDbg.ProjectSystem;

// Reads PiDbg MSBuild properties from the startup project.
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

        storage.GetPropertyValue("PiDbgHost", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var host);
        if (string.IsNullOrWhiteSpace(host)) return null;

        storage.GetPropertyValue("PiDbgUser", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var user);
        storage.GetPropertyValue("PiDbgSshPort", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var portStr);
        storage.GetPropertyValue("PiDbgSshKeyFile", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var keyFile);

        return new SshConnectionConfig
        {
            Host    = host,
            User    = string.IsNullOrEmpty(user) ? "pi" : user,
            Port    = int.TryParse(portStr, out var p) ? p : 22,
            KeyFile = string.IsNullOrEmpty(keyFile) ? null : keyFile,
        };
    }

    public async Task<string> GetAppNameAsync(CancellationToken ct)
    {
        var storage = await GetBuildPropertyStorageAsync(ct).ConfigureAwait(false);
        if (storage is null) return "MyApp";

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        storage.GetPropertyValue("PiDbgAppName", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var appName);
        if (!string.IsNullOrEmpty(appName)) return appName;

        storage.GetPropertyValue("AssemblyName", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var assemblyName);
        return string.IsNullOrEmpty(assemblyName) ? "MyApp" : assemblyName;
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
}
