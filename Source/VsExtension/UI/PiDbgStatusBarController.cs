using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PiDbg.UI;

internal static class PiDbgStatusBarController
{
    public static async Task SetTargetAsync(string host, AsyncPackage package, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var bar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        bar?.SetText($"Pi Debug: {host}");
    }

    public static async Task ClearAsync(AsyncPackage package, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var bar = await package.GetServiceAsync(typeof(SVsStatusbar)) as IVsStatusbar;
        bar?.SetText("");
    }
}
