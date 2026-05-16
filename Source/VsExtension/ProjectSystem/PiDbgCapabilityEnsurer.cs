using System.IO;
using System.Xml.Linq;

using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using PiDbg.Infrastructure;

namespace PiDbg.ProjectSystem;

internal sealed class PiDbgCapabilityEnsurer
{
    private readonly AsyncPackage _package;

    public PiDbgCapabilityEnsurer(AsyncPackage package) => _package = package;

    public async Task<string?> GetProjectPathAsync(CancellationToken ct)
    {
        var hier = await GetStartupHierarchyAsync(ct).ConfigureAwait(false);
        if (hier is null)
        {
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                "PiDbg: Could not determine startup project. " +
                "Make sure a project is set as the startup project in Solution Explorer.");
            return null;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        hier.GetCanonicalName((uint)VSConstants.VSITEMID.Root, out var path);

        if (string.IsNullOrEmpty(path))
        {
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                "PiDbg: Could not read the startup project path.");
            return null;
        }

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
        PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
            "PiDbg: Added PiDbg capability to project. Reloading...");

        // Reload so VS picks up the new capability and activates PiDbgDebugLaunchProvider.
        var solution = await _package.GetServiceAsync(typeof(SVsSolution)).ConfigureAwait(false) as IVsSolution;
        var solution4 = solution as IVsSolution4;
        if (solution4 is null)
        {
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                "PiDbg: Could not reload project (IVsSolution4 unavailable). Reload the project manually.");
            return;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

        var hier = await GetStartupHierarchyAsync(ct).ConfigureAwait(false);
        if (hier is null) return;

        ErrorHandler.ThrowOnFailure(solution.GetGuidOfProject(hier, out var projectGuid));
        solution4.ReloadProject(ref projectGuid);
    }

    private async Task<IVsHierarchy?> GetStartupHierarchyAsync(CancellationToken ct)
    {
        var bm = await _package.GetServiceAsync(typeof(SVsSolutionBuildManager))
            .ConfigureAwait(false) as IVsSolutionBuildManager2;

        if (bm is null)
        {
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                "PiDbg: IVsSolutionBuildManager2 unavailable.");
            return null;
        }

        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        bm.get_StartupProject(out var hier);
        return hier;
    }
}
