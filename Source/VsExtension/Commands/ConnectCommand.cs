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
            try
            {
                await RunAsync(pkg).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                VsShellUtilities.ShowMessageBox(
                    pkg,
                    $"Pi Debugger configuration failed:\n\n{ex.Message}",
                    "Pi Debugger",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        });
    }

    private static async Task RunAsync(AsyncPackage pkg)
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
            host:       existing?.Host       ?? "",
            user:       existing?.User       ?? "pi",
            port:       existing?.Port       ?? 22,
            rootFolder: existing?.RootFolder ?? "~/meadow",
            keyFile:    existing?.KeyFile,
            appName:    existingAppName);

        if (dlg.ShowDialog() != true) return;

        var config = new SshConnectionConfig
        {
            Host       = dlg.Host,
            Port       = dlg.Port,
            User       = dlg.User,
            RootFolder = dlg.RootFolder,
            KeyFile    = dlg.KeyFile,
            Password   = dlg.Password,
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
        else
        {
            PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
                "PiDbg: No startup project found — launchSettings.json was not written. " +
                "Set a startup project and run Connect again.");
        }

        await PiDbgStatusBarController.SetTargetAsync(dlg.Host, pkg, CancellationToken.None)
            .ConfigureAwait(false);

        var profileName = PiDbgLaunchProfile.BuildProfileName(dlg.Host);
        PiDbgPackage.OutputWindow.WriteLine(OutputPane.PiDbg,
            $"Pi Debugger configured: {dlg.User}@{dlg.Host}:{dlg.Port}  app={appName}. " +
            $"Select '{profileName}' in the toolbar dropdown and press F5 to deploy and debug.");

        // Activate must run on the UI thread.
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        PiDbgPackage.OutputWindow.Activate(OutputPane.PiDbg);
    }
}
