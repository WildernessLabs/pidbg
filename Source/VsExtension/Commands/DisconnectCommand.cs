using Microsoft.VisualStudio.Shell;
using PiDbg.Infrastructure;
using System.ComponentModel.Design;

namespace PiDbg.Commands;

// "Pi Debugger → Disconnect" menu command.
// Implemented in Phase 7 (P7.3).
internal sealed class DisconnectCommand
{
    private readonly AsyncPackage _package;
    private readonly SshConnectionManager _ssh;

    private DisconnectCommand(AsyncPackage package, SshConnectionManager ssh)
    {
        _package = package;
        _ssh = ssh;
    }

    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as IMenuCommandService
            ?? throw new InvalidOperationException("IMenuCommandService unavailable");

        var cmdId = new CommandID(PackageGuids.CommandSet, CommandIds.Disconnect);
        var cmd = new OleMenuCommand(Execute, cmdId);
        commandService.AddCommand(cmd);
    }

    private static void Execute(object sender, EventArgs e)
    {
        PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
            "Disconnect is not yet implemented. To stop Pi debugging, remove the PiDbg profile from launchSettings.json.");
    }
}
