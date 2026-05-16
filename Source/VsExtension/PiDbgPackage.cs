using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

using PiDbg.Commands;
using PiDbg.Infrastructure;

namespace PiDbg;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideOptionPage(typeof(UI.PiDbgOptionPage), "Pi Debugger", "Connection", 0, 0, true)]
[Guid(PackageGuids.PackageGuidString)]
public sealed class PiDbgPackage : AsyncPackage
{
    public static PiDbgPackage Current { get; private set; } = null!;

    public static IOutputWindowService OutputWindow { get; private set; } = null!;
    public static ISshConnectionManager Ssh { get; private set; } = null!;
    public static IGrpcChannelFactory GrpcChannels { get; private set; } = null!;
    public static IDebugTunnelManager Tunnels { get; private set; } = null!;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Current = this;

        await base.InitializeAsync(cancellationToken, progress);
        
        // Switch to UI thread for services that interact with the shell
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        try
        {
            // Initialize infrastructure services
            OutputWindow = new OutputWindowService(this);
            Ssh = new SshConnectionManager(OutputWindow);
            GrpcChannels = new GrpcChannelFactory();
            Tunnels = new DebugTunnelManager();

            await DebugOnPiCommand.InitializeAsync(this);
            await ConnectCommand.InitializeAsync(this);
            await DisconnectCommand.InitializeAsync(this);
            await ShowLogsCommand.InitializeAsync(this);
            await ExportDiagnosticsCommand.InitializeAsync(this);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PiDbgPackage initialization failed: {ex}");
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            (Ssh as IDisposable)?.Dispose();
            (GrpcChannels as IDisposable)?.Dispose();
            (Tunnels as IDisposable)?.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class PackageGuids
{
    public const string PackageGuidString = "6B4C9E01-5F2A-4D7B-9E8F-3A7B1C2D4E5F";
    public const string CommandSetGuidString = "2F8A1C3D-4E5B-6C7D-8E9F-0A1B2C3D4E5F";

    public static readonly Guid Package = new(PackageGuidString);
    public static readonly Guid CommandSet = new(CommandSetGuidString);
}

internal static class CommandIds
{
    public const int DebugOnPi = 0x0104;
    public const int Connect = 0x0100;
    public const int Disconnect = 0x0101;
    public const int ShowLogs = 0x0102;
    public const int ExportDiagnostics = 0x0103;
}
