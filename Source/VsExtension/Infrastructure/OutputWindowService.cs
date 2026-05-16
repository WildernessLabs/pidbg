using System;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PiDbg.Infrastructure;

internal sealed class OutputWindowService : IOutputWindowService
{
    private static readonly Guid PiDbgPaneGuid = new Guid("B1C2D3E4-5A6B-7C8D-9E0F-A1B2C3D4E5F6");

    private readonly IVsOutputWindowPane _pidbgPane;

    public OutputWindowService(AsyncPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var outputWindow = (IVsOutputWindow)((IServiceProvider)package).GetService(typeof(SVsOutputWindow));
        _pidbgPane = GetOrCreatePane(outputWindow, PiDbgPaneGuid, "PiDbg");
    }

    public void Write(OutputPane pane, string message)
        => GetPane(pane).OutputStringThreadSafe(message);

    public void WriteLine(OutputPane pane, string message)
        => GetPane(pane).OutputStringThreadSafe(
            $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");

    public void WriteError(OutputPane pane, string message)
        => WriteLine(pane, $"ERROR: {message}");

    public void WriteWarning(OutputPane pane, string message)
        => WriteLine(pane, $"WARN:  {message}");

    public void Clear(OutputPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane(pane).Clear();
    }

    public void Activate(OutputPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane(pane).Activate();
    }

    private IVsOutputWindowPane GetPane(OutputPane pane) => _pidbgPane;

    private static IVsOutputWindowPane GetOrCreatePane(
        IVsOutputWindow window, Guid paneGuid, string paneName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        window.GetPane(ref paneGuid, out var pane);
        if (pane is null)
        {
            window.CreatePane(ref paneGuid, paneName, fInitVisible: 1, fClearWithSolution: 0);
            window.GetPane(ref paneGuid, out pane);
        }
        return pane!;
    }
}
