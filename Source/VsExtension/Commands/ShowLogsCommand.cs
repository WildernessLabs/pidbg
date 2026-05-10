using Microsoft.VisualStudio.Shell;
using System.ComponentModel.Design;

namespace PiDbg.Commands;

// "Pi Debugger → Show Device Logs" menu command.
// Implemented in Phase 7 (P7.3).
internal sealed class ShowLogsCommand
{
    private readonly AsyncPackage _package;

    private ShowLogsCommand(AsyncPackage package) => _package = package;

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as IMenuCommandService
            ?? throw new InvalidOperationException("IMenuCommandService unavailable");

        var cmdId = new CommandID(PackageGuids.CommandSet, CommandIds.ShowLogs);
        commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
    }

    private static void Execute(object sender, EventArgs e)
        => throw new NotImplementedException("Implemented in Phase 7");
}
