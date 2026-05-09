# PiDbg VSIX — UI and Commands

---

## 1. Command Table (VSCT)

All commands are declared in `PiDbgPackage.vsct`. Commands use GUIDs from
`PiDbgPackage.cs` to avoid conflicts with other extensions.

### Menu structure

```
Tools
└── Remote Devices...         [Ctrl+Shift+R, D]    Opens Device Manager window

View
└── Other Windows
    └── PiDbg Log Viewer       Opens Log Viewer window

Debug
└── Deploy to Raspberry Pi     Context-sensitive: visible when active project
                               has a Pi launch profile
```

### Toolbar (PiDbg toolbar, shown in Debug toolbar area)
```
[Connection status indicator]  [Device dropdown]  [Deploy ▼]
```
The toolbar is hidden until a project with a Pi profile is active.

### Context menu additions

**Solution Explorer → Project → right-click:**
```
Deploy to Raspberry Pi
```

### Command IDs

```csharp
internal static class PiDbgCommandIds
{
    public const int OpenDeviceManager  = 0x0100;
    public const int OpenLogViewer      = 0x0101;
    public const int DeployProject      = 0x0102;
    public const int AddDevice          = 0x0103;
    public const int ProvisionDevice    = 0x0104;
    public const int TestConnection     = 0x0105;
}
```

---

## 2. Command Handler Pattern

All commands derive from a common base that handles the threading and error display:

```csharp
internal abstract class PiDbgCommandBase
{
    protected PiDbgPackage Package { get; }
    protected IVsOutputWindowService Output { get; }

    protected abstract Task ExecuteCoreAsync(
        OleMenuCmdEventArgs args, CancellationToken ct);

    // The VS command callback (registered on UI thread)
    protected void Execute(object sender, EventArgs args)
    {
        Package.JoinableTaskFactory.RunAsync(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            try
            {
                await ExecuteCoreAsync((OleMenuCmdEventArgs)args, cts.Token);
            }
            catch (OperationCanceledException)
            {
                await Output.WriteLineAsync("[PiDbg] Operation cancelled.");
            }
            catch (PiDbgException ex)
            {
                await Output.WriteErrorAsync(ex);
                await ShowInfoBarAsync(ex.Message, ex.Guidance);
            }
            catch (Exception ex)
            {
                await Output.WriteCriticalErrorAsync(ex);
            }
        }).FireAndForget(); // JTF extension — tracks task, surfaces exceptions
    }
}
```

---

## 3. Device Manager Window

### Window registration
```csharp
[Guid(DeviceManagerWindow.WindowGuidString)]
internal sealed class DeviceManagerWindow : ToolWindowPane
{
    public const string WindowGuidString = "a1b2c3d4-...";

    public DeviceManagerWindow()
    {
        Caption = "PiDbg — Remote Devices";
        BitmapResourceID = 301;
        BitmapIndex = 0;
    }

    protected override void Initialize()
    {
        var vm = Package.DiContainer.GetRequiredService<DeviceManagerViewModel>();
        Content = new DeviceManagerWindowControl { DataContext = vm };
    }
}
```

### Layout

```
┌──────────────────────────────────────────────────────────────────────┐
│ PiDbg — Remote Devices                                    [+] [?]   │
├──────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  ┌─────────────────────────────────────────────────────────────────┐ │
│  │ ● Dev Board          192.168.1.100    Connected    [•••]       │ │
│  │ ○ Lab Pi 4           192.168.1.101    Not tested   [•••]       │ │
│  │ ○ Headless Pi Zero   raspberrypi2     Unreachable  [•••]       │ │
│  └─────────────────────────────────────────────────────────────────┘ │
│                                                                      │
│  [+ Add Device]  [✏ Edit]  [🗑 Remove]                              │
│                                                                      │
├──────────────────────────────────────────────────────────────────────┤
│  Dev Board (192.168.1.100)                                           │
│  ─────────────────────────                                           │
│  Agent:   v1.1.0    vsdbg: v17.x    .NET: 10.0.1                   │
│  Disk:    4.2 GB free / 15.9 GB                                      │
│                                                                      │
│  [Test Connection]  [Provision Device]  [Deploy]  [Show Logs]        │
│                                                                      │
│  ▼ Active Sessions (1)                                               │
│    MyApp · PID 4829 · vsdbg 4823 · port 4024  [Stop]               │
│                                                                      │
└──────────────────────────────────────────────────────────────────────┘
```

### DeviceManagerViewModel

```csharp
internal sealed class DeviceManagerViewModel : ObservableObject
{
    // Bound to list
    public ObservableCollection<DeviceItemViewModel> Devices { get; } = new();

    // Bound to detail panel
    public DeviceItemViewModel? SelectedDevice { get => ...; set => ... }

    // Commands (RelayCommand<T> from CommunityToolkit.Mvvm)
    public IRelayCommand AddDeviceCommand { get; }
    public IRelayCommand<DeviceItemViewModel> EditDeviceCommand { get; }
    public IRelayCommand<DeviceItemViewModel> RemoveDeviceCommand { get; }
    public IAsyncRelayCommand<DeviceItemViewModel> TestConnectionCommand { get; }
    public IAsyncRelayCommand<DeviceItemViewModel> ProvisionDeviceCommand { get; }
    public IAsyncRelayCommand<DeviceItemViewModel> DeployCommand { get; }
    public IRelayCommand<DeviceItemViewModel> ShowLogsCommand { get; }
}
```

### Add/Edit Device Dialog

```
┌───────────────────────────────────────────────────────┐
│  Add Raspberry Pi Device                         [X]  │
├───────────────────────────────────────────────────────┤
│                                                       │
│  Friendly name:  [Dev Board                      ]   │
│  Hostname / IP:  [192.168.1.100                  ]   │
│  SSH port:       [22           ]                      │
│  Username:       [pi           ]                      │
│                                                       │
│  Authentication:                                      │
│  ○ SSH key file   Path: [~/.pidbg/keys/... ] [...]   │
│  ● Password       (stored in Windows Credential Mgr) │
│                                                       │
│  Default deploy path:                                 │
│  [ /opt/pidbg/apps                              ]    │
│                                                       │
│  Startup command override (optional):                 │
│  [                                              ]    │
│                                                       │
│  [Test Connection]                                    │
│  ✓ Connection successful — agent v1.1.0, .NET 10.0.1 │
│                                                       │
│             [Cancel]  [Save]                          │
└───────────────────────────────────────────────────────┘
```

"Test Connection" is async: runs on background thread, shows spinner on button,
updates status line below. Can be cancelled by the dialog closing.

---

## 4. Progress Reporting

Three progress surfaces, used simultaneously for different granularities:

### VS Status Bar
Coarse-grained: one line, reflects current phase.
```
PiDbg: Deploying MyApp... (75%)
PiDbg: Starting vsdbg...
PiDbg: Debugger attached — MyApp (PID 4829)
```

```csharp
internal sealed class VsStatusBarService : IVsStatusBarService
{
    public async Task SetTextAsync(string text, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        _statusBar.SetText(text);
    }

    public async Task SetProgressAsync(string label, uint current, uint total, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        _statusBar.Progress(ref _progressCookie, 1, label, current, total);
    }
}
```

### Output Window
Fine-grained: every file, every step. The developer can read exactly what happened.
Serilog `Information` level and above. See `05-infrastructure.md §2`.

### Device Manager Panel
The selected device's detail panel shows a live progress bar during deploy:
```
Deploying: [████████░░░░] 8/15 files  2.1 MB / 4.1 MB
```

Progress is driven by `IProgress<DeploymentProgress>` flowing through
`DeploymentProgressViewModel` which is updated via `Dispatcher.InvokeAsync`.

---

## 5. Log Viewer Window

A second tool window that shows the real-time log stream from the agent (the `StreamLogs`
gRPC server-streaming RPC).

```
┌──────────────────────────────────────────────────────────────────────┐
│ PiDbg Log Viewer — Dev Board                      [▶ Stream] [✕]   │
├──────────────────────────────────────────────────────────────────────┤
│ Filter: [         ] Level: [Information ▼]  Source: [All       ▼]   │
├──────────────────────────────────────────────────────────────────────┤
│ 10:23:45.123  INF  [Agent]   Deployment committed: MyApp 15 files   │
│ 10:23:45.456  INF  [Agent]   vsdbg launched PID=4823 port=4024      │
│ 10:23:46.001  INF  [vsdbg]   Debugger attached to PID 4829          │
│ 10:23:47.882  INF  [App]     Application started                     │
│ ...                                                                  │
│                                                              [Clear] │
└──────────────────────────────────────────────────────────────────────┘
```

### Implementation
The log stream is an `IAsyncEnumerable<LogEvent>` from `IAgentClient.StreamLogsAsync()`.
A background `Task` consumes it and appends to an `ObservableCollection<LogEventViewModel>`
(max 10,000 entries; older entries dropped). The collection is updated via
`DispatcherQueue` (WPF) — never blocks the UI thread.

The filter fields are bound to the `LogViewerViewModel` and filter client-side — the
stream receives all `Information+` events and the VM applies the text/level/source filter
before adding to the visible collection.

---

## 6. Provision Device Button

"Provision" runs the `install-agent.sh` script on the Pi via SSH. This is a one-time
operation for new devices.

Steps:
1. Connect SSH
2. Check prerequisites (`dotnet`, `curl`, `systemctl`)
3. Upload `install-agent.sh` via SFTP to `/tmp/`
4. Execute `bash /tmp/install-agent.sh` — streams stdout/stderr to Output window
5. Verify agent starts: ping with 30-second timeout
6. Update device record with agent version + capabilities
7. Show success in Output window + Device Manager detail

Progress is shown as scrolling output in the Output window (the same approach used by
Docker's "Pull Image" output). A modal progress spinner appears on the Provision button.

---

## 7. Error UX

### Hierarchy of error surfaces

| Severity | Surface | Example |
|---|---|---|
| Info | Output window only | "Delta: 2 files changed, uploading" |
| Warning | InfoBar (dismissable) | "Agent update available — will apply after session" |
| Recoverable error | Output window + InfoBar | "SHA-256 mismatch — press F5 to redeploy" |
| Fatal (blocks session) | Output window + Error List row | "SSH auth failed — check credentials" |
| Unexpected/bug | Output window + dialog | "Internal error (ID: 7f3a2b). Please report." |

### InfoBar placement
InfoBar appears at the top of the active document editor (not in the tool window) for
errors that the developer needs to act on while editing code:

```csharp
internal sealed class VsInfoBarService : IVsInfoBarService
{
    public async Task ShowWarningAsync(string message, string? actionLabel,
        Func<Task>? action, CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var host = FindInfoBarHost(); // top of active document
        var model = new InfoBarModel(
            new IVsInfoBarElement[]
            {
                new InfoBarTextSpan(message),
                action != null ? new InfoBarHyperlink(actionLabel!) : null!
            }.WhereNotNull(),
            KnownMonikers.StatusWarning,
            isCloseButtonVisible: true);
        var ui = _infoBarFactory.CreateInfoBar(model);
        if (action != null)
            ui.Advise(new InfoBarEventHandler(action), out _);
        host.AddInfoBar(ui);
    }
}
```

### Error List integration
Deployment errors that have a specific file + line are added to the Error List with a
task provider so the developer can double-click to navigate:
```csharp
// Rarely needed — mostly for manifest validation errors
_errorListProvider.Tasks.Add(new ErrorTask
{
    ErrorCategory = TaskErrorCategory.Error,
    Text = $"SHA-256 mismatch: {fileName}",
    Category = TaskCategory.BuildCompile,
});
```

### No unexpected dialogs
The extension never shows an unexpected `MessageBox`. The only modal dialogs are:
- "Add/Edit Device" (user-initiated)
- First-run "vsdbg install" confirmation (with "Don't ask again" option)
- "Provision device" confirmation (destructive operation)
