using System.IO;
using System.Xml.Linq;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PiDbg.ProjectSystem;

internal sealed class PiDbgCapabilityEnsurer
{
    private readonly AsyncPackage _package;

    public PiDbgCapabilityEnsurer(AsyncPackage package) => _package = package;

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

    public async Task EnsureAsync(CancellationToken ct)
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
