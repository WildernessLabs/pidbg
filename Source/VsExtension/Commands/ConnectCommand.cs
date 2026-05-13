using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.ComponentModel.Design;

using PiDbg.Infrastructure;
using PiDbg.UI;
using PsReader = PiDbg.ProjectSystem.ProjectPropertyReader;

namespace PiDbg.Commands;

internal sealed class ConnectCommand
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
        var commandService = await package.GetServiceAsync(typeof(IMenuCommandService))
            as IMenuCommandService
            ?? throw new InvalidOperationException("IMenuCommandService unavailable");

        var cmdId = new CommandID(PackageGuids.CommandSet, CommandIds.Connect);
        commandService.AddCommand(new OleMenuCommand(Execute, cmdId));
    }

    private static void Execute(object sender, EventArgs e)
    {
        var pkg = PiDbgPackage.Current!;
        pkg.JoinableTaskFactory.RunAsync(async () =>
        {
            var reader  = new PsReader(pkg);
            var config  = await reader.GetConnectionConfigAsync(CancellationToken.None).ConfigureAwait(false);
            var appName = await reader.GetAppNameAsync(CancellationToken.None).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dlg = new ConnectDialog();
            dlg.SetDefaults(
                host:    config?.Host    ?? "",
                user:    config?.User    ?? "pi",
                port:    config?.Port    ?? 22,
                keyFile: config?.KeyFile,
                appName: appName);

            if (dlg.ShowDialog() != true) return;

            await reader.SetConnectionConfigAsync(
                new SshConnectionConfig
                {
                    Host    = dlg.Host,
                    Port    = dlg.Port,
                    User    = dlg.User,
                    KeyFile = dlg.KeyFile,
                },
                CancellationToken.None).ConfigureAwait(false);

            var resolvedAppName = string.IsNullOrWhiteSpace(dlg.AppName) ? appName : dlg.AppName;
            await reader.SetAppNameAsync(resolvedAppName, CancellationToken.None).ConfigureAwait(false);
            await reader.EnsurePiDbgCapabilityAsync(CancellationToken.None).ConfigureAwait(false);

            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                $"Pi Debugger configured: {dlg.User}@{dlg.Host}:{dlg.Port}  app={resolvedAppName}. " +
                "Press F5 to deploy and debug.");
        }).FileAndForget("PiDbg/ConnectCommand");
    }
}
