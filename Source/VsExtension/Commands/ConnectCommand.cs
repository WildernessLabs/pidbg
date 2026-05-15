using System.ComponentModel.Design;
using System.IO;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using PiDbg.Debug;
using PiDbg.Infrastructure;
using PiDbg.ProjectSystem;
using PiDbg.UI;

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
            var ensurer = new PiDbgCapabilityEnsurer(pkg);
            var projectPath = await ensurer.GetProjectPathAsync(CancellationToken.None)
                .ConfigureAwait(false);

            // Pre-fill dialog from existing profile if present.
            SshConnectionConfig? existing = null;
            string existingAppName = "";
            if (projectPath is not null)
            {
                var read = await PiDbgLaunchProfileWriter.TryReadAsync(projectPath, CancellationToken.None)
                    .ConfigureAwait(false);
                existing = read?.Config;
                existingAppName = read?.AppName ?? "";
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var dlg = new ConnectDialog();
            dlg.SetDefaults(
                host:    existing?.Host    ?? "",
                user:    existing?.User    ?? "pi",
                port:    existing?.Port    ?? 22,
                keyFile: existing?.KeyFile,
                appName: existingAppName);

            if (dlg.ShowDialog() != true) return;

            var config = new SshConnectionConfig
            {
                Host    = dlg.Host,
                Port    = dlg.Port,
                User    = dlg.User,
                KeyFile = dlg.KeyFile,
            };

            var appName = string.IsNullOrWhiteSpace(dlg.AppName)
                ? (projectPath is null ? "MyApp" : Path.GetFileNameWithoutExtension(projectPath))
                : dlg.AppName;

            if (projectPath is not null)
            {
                await PiDbgLaunchProfileWriter.WriteAsync(
                    projectPath, config, appName, CancellationToken.None)
                    .ConfigureAwait(false);

                await ensurer.EnsureAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await PiDbgStatusBarController.SetTargetAsync(
                dlg.Host, pkg, CancellationToken.None).ConfigureAwait(false);

            var profileName = PiDbgLaunchProfile.BuildProfileName(dlg.Host);
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                $"Pi Debugger configured: {dlg.User}@{dlg.Host}:{dlg.Port}  app={appName}. " +
                $"Select '{profileName}' in the toolbar dropdown and press F5 to deploy and debug.");
        }).FileAndForget("PiDbg/ConnectCommand");
    }
}
