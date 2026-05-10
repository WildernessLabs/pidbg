using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using System.Runtime.InteropServices;

namespace PiDbg.Infrastructure;

// Manages the "Pi Debugger" and "Pi Debugger - Device Log" Output window panes.
// Implemented in Phase 4 (P4.5).
internal sealed class OutputPaneManager
{
    private static OutputPaneManager? _instance;

    private IVsOutputWindowPane? _generalPane;
    private IVsOutputWindowPane? _deviceLogPane;

    private static readonly Guid GeneralPaneGuid = new("A1F2B3C4-D5E6-7890-ABCD-EF1234567890");
    private static readonly Guid DeviceLogPaneGuid = new("B2C3D4E5-E6F7-8901-BCDE-F12345678901");

    public static Task InitializeAsync(AsyncPackage package)
        => throw new NotImplementedException("Implemented in Phase 4");

    public static OutputPaneManager Instance =>
        _instance ?? throw new InvalidOperationException("OutputPaneManager not initialized");

    public void WriteGeneral(string message)
        => throw new NotImplementedException("Implemented in Phase 4");

    public void WriteDeviceLog(string message)
        => throw new NotImplementedException("Implemented in Phase 4");

    public void ActivateGeneralPane()
        => throw new NotImplementedException("Implemented in Phase 4");
}
