using Microsoft.VisualStudio.Shell;
using PiDbg.Diagnostics;
using System.ComponentModel.Design;

namespace PiDbg.Commands;

// "Pi Debugger → Export Diagnostics Bundle" menu command.
// Implemented in Phase 7 (P7.8).
internal sealed class ExportDiagnosticsCommand
{
    private readonly AsyncPackage _package;
    private readonly DiagnosticsBundleExporter _exporter;

    private ExportDiagnosticsCommand(AsyncPackage package, DiagnosticsBundleExporter exporter)
    {
        _package = package;
        _exporter = exporter;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as IMenuCommandService
            ?? throw new InvalidOperationException("IMenuCommandService unavailable");

        var cmdId = new CommandID(PackageGuids.CommandSet, CommandIds.ExportDiagnostics);
        commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
    }

    private static void Execute(object sender, EventArgs e)
        => throw new NotImplementedException("Implemented in Phase 7");
}
