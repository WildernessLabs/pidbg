# Phase 7 — Polish

Completes the remaining cross-cutting concerns: output formatting, progress reporting,
user-facing commands, project property UI, daemon health reporting, self-update, and
deployment scripts. All tasks are largely independent and can be parallelised.

---

## P7.1 — Error Surface and Output Window Formatting

**Purpose**: Ensure all user-facing errors are clear, actionable, and consistently
formatted so the developer immediately knows what went wrong and what to do about it.

**Dependencies**: P4.5, P6.5

**Files**:
- `Source/VsExtension/UI/ErrorSurface.cs`
- `Source/VsExtension/UI/OutputWindowService.cs` (extend)

**Implementation details**:

Define a consistent format for every provisioning and deployment error:
```
[HH:MM:SS] ERROR: <what failed>
           Reason: <why it failed>
           Action: <what the user should do>
```

Example:
```
[14:23:04] ERROR: Platform check failed: /opt/meadow not found
           Reason: The device directory skeleton has not been created.
           Action: Run the host bootstrap script on the device:
                     curl -sSL https://.../setup-meadow.sh | sudo bash
```

Add these helper methods to `OutputWindowService`:
```csharp
public void WriteProvisioningError(OutputPane pane, string what, string reason, string action)
{
    WriteLine(pane, $"ERROR: {what}");
    WriteLine(pane, $"       Reason: {reason}");
    WriteLine(pane, $"       Action: {action}");
}

public void WriteSection(OutputPane pane, string title)
{
    var line = new string('─', 60);
    WriteLine(pane, line);
    WriteLine(pane, $"  {title}");
    WriteLine(pane, line);
}
```

Define error templates for every known failure mode (extract from
`docs/vsix/10-provisioning-system.md §12`):

| Failure | What | Reason | Action |
|---|---|---|---|
| Wrong arch | Architecture not supported | Device is 32-bit ARM | Use a 64-bit Raspberry Pi OS image |
| OS too old | OS version not supported | OS < Bookworm | Upgrade to Raspberry Pi OS 64-bit Bookworm |
| No /opt/meadow | Device not bootstrapped | setup-meadow.sh not run | Run setup-meadow.sh with sudo |
| Disk full | Insufficient disk space | < 200 MB free | Free disk space on the device |
| Daemon timeout | Daemon did not start | Service failed | Check journalctl, run PiDbg: Diagnose |
| Deploy verify failed | Integrity check failed | File transfer corruption | Retry; check network stability |
| Session start failed | Debug session not created | vsdbg failed to start | Run PiDbg: Diagnose |

Add `PiDbgException` hierarchy:
```csharp
public class PiDbgException : Exception
{
    public string What   { get; }
    public string Reason { get; }
    public string Action { get; }
    // ...
}
public class ProvisioningException : PiDbgException { }
public class DeploymentException   : PiDbgException { }
public class DebugSessionException : PiDbgException { }
```

In `PiDbgDebugLaunchProvider.QueryDebugTargetsAsync`, wrap the entire pipeline in:
```csharp
catch (PiDbgException ex)
{
    output.WriteProvisioningError(OutputPane.PiDbg, ex.What, ex.Reason, ex.Action);
    throw;  // VS will show the exception message in a dialog
}
catch (Exception ex)
{
    output.WriteError(OutputPane.PiDbg, $"Unexpected error: {ex.Message}");
    throw;
}
```

**Edge cases**:
- VS shows an error dialog when `QueryDebugTargetsAsync` throws. The dialog message
  comes from `Exception.Message`. Keep `PiDbgException.Message` user-friendly (the
  `What` field, not a stack trace).
- Do not swallow exceptions silently. Every caught exception must be logged and
  re-thrown (or surfaced in the Output window).

**Testing requirements**:
- Manual test: trigger each known failure mode and verify the Output window shows
  the correct what/reason/action triplet
- Unit test: `PiDbgException` hierarchy compiles and is constructible

**Definition of done**:
- [ ] `PiDbgException` hierarchy with What/Reason/Action properties
- [ ] `WriteProvisioningError` formats three-line error in Output window
- [ ] All 7 known failure modes have corresponding error templates
- [ ] Top-level exception handler in `QueryDebugTargetsAsync`
- [ ] Error messages are actionable (tell the user exactly what to do)

---

## P7.2 — Progress Reporting

**Purpose**: Show deployment progress in the VS status bar and Output window so the
developer has feedback during the 3–15 second F5 cycle.

**Dependencies**: P4.5, P4.8

**Files**:
- `Source/VsExtension/UI/ProgressReporter.cs`

**Implementation details**:

```csharp
public sealed class ProgressReporter : IDisposable
{
    private readonly IVsStatusbar _statusbar;
    private readonly IOutputWindowService _output;
    private uint _cookie;

    public ProgressReporter(AsyncPackage package, IOutputWindowService output)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _statusbar = (IVsStatusbar)package.GetService(typeof(SVsStatusbar))!;
        _output    = output;
    }

    public void Report(string phase, int percentComplete, string detail = "")
    {
        // VS status bar text (UI thread)
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _statusbar.Progress(ref _cookie, 1, $"PiDbg: {phase}", (uint)percentComplete, 100);
            _statusbar.SetText($"PiDbg: {phase} {percentComplete}%");
        });

        // Output window (any thread)
        if (!string.IsNullOrEmpty(detail))
            _output.WriteLine(OutputPane.PiDbg, $"  [{phase}] {percentComplete}% {detail}");
    }

    public void Complete(string message)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _statusbar.Progress(ref _cookie, 0, "", 0, 0);  // hide progress bar
            _statusbar.SetText($"PiDbg: {message}");
        });
        _output.WriteLine(OutputPane.PiDbg, message);
    }

    public void Dispose() => Complete("Ready");
}
```

Phase-to-percentage mapping for a typical F5 cycle:
```
Provisioning:  0–15%   (detection, validation, health check)
Publish:       15–35%  (dotnet publish, SHA-256 computation)
Upload:        35–85%  (SFTP transfer of changed files)
Activation:    85–90%  (directory move/symlink swap)
Session Start: 90–95%  (vsdbg launch, port binding)
Attaching:     95–100% (VS engine connecting)
```

**Edge cases**:
- `IVsStatusbar.Progress` requires the UI thread. The `JoinableTaskFactory.RunAsync`
  pattern fires and forgets the UI update. This is acceptable — progress updates are
  advisory; missing one update is not a correctness issue.
- The `_cookie` parameter for `IVsStatusbar.Progress` is an out parameter on first call
  (pass 0) and a ref parameter on subsequent calls. After `Progress(0, ...)` to hide,
  reset `_cookie = 0` for the next deployment.
- Cancellation: if the user presses the VS "Stop" button mid-deploy, the status bar
  should show "PiDbg: Cancelled". Ensure `Dispose` is called in a `finally` block.

**Testing requirements**:
- Manual test: press F5, observe status bar shows phase names and percentages
- Manual test: deployment completes — status bar returns to idle state
- Manual test: cancel mid-deploy — status bar shows "Cancelled"

**Definition of done**:
- [ ] `ProgressReporter` updates VS status bar on UI thread
- [ ] Phase-to-percentage mapping covers full F5 cycle
- [ ] `Complete()` hides progress bar
- [ ] `Dispose()` called in `finally` blocks (never leave progress bar stuck)
- [ ] Output window also receives phase updates

---

## P7.3 — PiDbg Commands

**Purpose**: Implement the four PiDbg commands accessible from the Tools menu and command
palette: Repair, Diagnostics, Uninstall, and Export Bundle.

**Dependencies**: P4.2, P6.5, P6.1

**Files**:
- `Source/VsExtension/Commands/RepairConnectionCommand.cs`
- `Source/VsExtension/Commands/RunDiagnosticsCommand.cs`
- `Source/VsExtension/Commands/UninstallCommand.cs`
- `Source/VsExtension/Commands/ExportDiagnosticsBundleCommand.cs`

**Implementation details**:

Each command follows the VS SDK `OleMenuCommand` pattern:

```csharp
public sealed class RepairConnectionCommand
{
    public static async Task InitializeAsync(AsyncPackage package)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var cmdService = await package.GetServiceAsync(typeof(IMenuCommandService))
                         as IMenuCommandService;
        var cmdId = new CommandID(PiDbgPackage.CommandSetGuid, 0x0101);
        var cmd   = new OleMenuCommand(Execute, cmdId);
        cmdService?.AddCommand(cmd);
    }

    private static void Execute(object sender, EventArgs e)
    {
        _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            // Clear provisioning cache for the active project's host
            ProvisioningOrchestrator.InvalidateCache(activeHost);
            // Re-run provisioning
            var output = GetService<IOutputWindowService>();
            output.Activate(OutputPane.Provisioning);
            output.WriteSection(OutputPane.Provisioning, "Repair Connection");
            // ... run ProvisioningOrchestrator
        });
    }
}
```

**RunDiagnosticsCommand**:
1. Connect SSH (reuse existing session if available)
2. Run `diag.sh` via SSH (embedded resource, same as detect.sh pattern)
3. If daemon is running: also call `GetDeviceInfo` gRPC
4. Display result in the "PiDbg Provisioning" pane
5. Output in the structured text format from `docs/vsix/10-provisioning-system.md §12`

**UninstallCommand**:
1. Show confirmation dialog: "Remove PiDbg from {host}? Apps and configuration will be preserved."
2. On confirm:
   - SSH: `systemctl --user stop meadow-daemon; systemctl --user disable meadow-daemon`
   - SSH: `pkill -u $USER vsdbg-ui || true`
   - SSH: `rm -f /opt/meadow/bin/meadow-daemon /opt/meadow/bin/meadow-daemon.bak`
   - SSH: `rm -rf /opt/meadow/vsdbg`
   - SSH: `rm -f ~/.config/systemd/user/meadow-daemon.service`
   - SSH: `systemctl --user daemon-reload`
   - SSH: `rm -f /etc/meadow/daemon.conf`
3. Invalidate provisioning cache
4. Show: "PiDbg uninstalled from {host}. Apps preserved at /opt/meadow/apps."

**ExportDiagnosticsBundleCommand**:
1. Run diagnostics (same as RunDiagnosticsCommand)
2. Collect:
   - `detection.json`: JSON from CapabilityDetector
   - `diag.txt`: output of diag.sh
   - `daemon-logs.txt`: last 500 lines via SSH `journalctl --user-unit meadow-daemon -n 500`
   - `service-file.txt`: content of `meadow-daemon.service`
   - `provision-log.jsonl`: VSIX provisioning log from `%LOCALAPPDATA%\PiDbg\Logs\`
3. Create ZIP: `pidbg-diag-{hostname}-{timestamp}.zip`
4. Save to `Downloads` folder
5. Open the Downloads folder in Explorer

**Edge cases**:
- Uninstall confirmation dialog must be modal and default to "Cancel" (not "Confirm")
  to prevent accidental uninstall.
- `ExportDiagnosticsBundleCommand` must handle the case where the daemon is not running
  (skip gRPC log collection gracefully, still produce the bundle).

**Testing requirements**:
- Manual test: Repair clears cache and re-runs provisioning
- Manual test: Diagnose shows structured output in Output window
- Manual test: Uninstall removes daemon, service, and vsdbg; leaves apps
- Manual test: Export Bundle creates a valid zip file in Downloads

**Definition of done**:
- [ ] All 4 commands registered in `PiDbgPackage.InitializeAsync`
- [ ] All 4 commands callable from Tools menu
- [ ] Repair invalidates cache and re-runs ProvisioningOrchestrator
- [ ] Diagnose shows structured report
- [ ] Uninstall prompts for confirmation before executing
- [ ] Export Bundle creates ZIP with all 5 diagnostic files

---

## P7.4 — Project Properties Page

**Purpose**: Add a PiDbg settings tab to the project Properties dialog so developers
can configure `PiDbgHost`, `PiDbgUser`, `PiDbgSshPort`, and `PiDbgAppName` without
editing the `.csproj` file manually.

**Dependencies**: P4.4, P4.2

**Files**:
- `Source/VsExtension/UI/PiDbgPropertyPage.cs`
- `Source/VsExtension/UI/PiDbgPropertyPageControl.xaml` + `.cs`

**Implementation details**:

Property page registration via package attribute:
```csharp
[ProvideProjectFactory(typeof(PiDbgProjectFactory), ...)]
// or use the standard VS property page mechanism:
[ProvideOptionPage(typeof(PiDbgOptionPage), "PiDbg", "Connection", 0, 0, true)]
```

Alternatively, implement as a project property page using `IVsPropertyPage2`:

```csharp
[Guid("D1E2F3A4-...")]
public sealed class PiDbgPropertyPage : IVsPropertyPage2
{
    private PiDbgPropertyPageControl _control;
    private IVsBuildPropertyStorage _storage;
    private bool _dirty;

    // Load properties into UI
    public int Apply()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _storage.SetPropertyValue("PiDbgHost", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, _control.HostTextBox.Text);
        _storage.SetPropertyValue("PiDbgUser", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, _control.UserTextBox.Text);
        // ... other properties
        _dirty = false;
        return VSConstants.S_OK;
    }
}
```

`PiDbgPropertyPageControl.xaml` — WPF UserControl with:
- `PiDbgHost`: text field, label "Raspberry Pi hostname or IP", placeholder "raspberrypi.local"
- `PiDbgUser`: text field, label "SSH username", default "pi"
- `PiDbgSshPort`: numeric input, label "SSH port", default 22
- `PiDbgSshKeyFile`: text field + Browse button, label "SSH key file (optional)"
- `PiDbgAppName`: text field, label "App name (leave blank to use project name)"
- "Test Connection" button that validates SSH connectivity
- Link: "Show setup instructions" → opens `docs/vsix/10-provisioning-system.md` in browser

**Edge cases**:
- The "Test Connection" button must be async. Use `JoinableTaskFactory.RunAsync` to avoid
  blocking the UI thread.
- Property pages in VS require `IVsPropertyPage2` implementation. The `[Guid]` attribute
  must be a unique GUID registered in the VSIX manifest/pkgdef.
- When `PiDbgSshKeyFile` is empty, the extension uses credential store password auth.
  The Browse button opens a standard file picker filtered to `.pem` and `*.` (no extension).

**Testing requirements**:
- Manual test: open project Properties, verify "PiDbg" tab appears
- Manual test: enter host/user, click OK, verify values saved to `.csproj`
- Manual test: "Test Connection" button connects and shows success/failure

**Definition of done**:
- [ ] Property page appears in project Properties dialog under "PiDbg" tab
- [ ] All 5 fields present with correct defaults
- [ ] "Test Connection" button works asynchronously
- [ ] Values saved to `.csproj` as MSBuild properties on "Apply" / "OK"
- [ ] Browse button for key file opens file picker

---

## P7.5 — HealthReporterService

**Purpose**: Broadcast periodic health snapshots to all active `StreamHealth` gRPC
subscribers, so the VSIX can show live device status without polling.

**Dependencies**: P2.3, P5.1, P5.7, P1.9

**Files**:
- `Source/Meadow.Daemon/Services/HealthReporterService.cs`

**Implementation details**:

```csharp
public sealed class HealthReporterService : BackgroundService
{
    private readonly Channel<HealthStatus> _channel =
        Channel.CreateBounded<HealthStatus>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private readonly IProcessManager _processManager;
    private readonly IDebugSessionManager _sessionManager;
    private readonly DaemonOptions _options;
    private readonly ILogger<HealthReporterService> _logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var health = BuildHealthStatus();
            _channel.Writer.TryWrite(health);
        }
    }

    public HealthStatus GetCurrentHealth() => BuildHealthStatus();

    public IAsyncEnumerable<HealthStatus> Subscribe(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    private HealthStatus BuildHealthStatus()
    {
        var proc = Process.GetCurrentProcess();
        return new HealthStatus
        {
            State            = HealthState.Healthy,
            UptimeSeconds    = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
            MemoryBytes      = proc.WorkingSet64,
            ActiveSessions   = _sessionManager.GetActiveSessionCount(),
            AppHealthItems   = { BuildAppHealthItems() },
        };
    }
}
```

In `MeadowDaemonGrpcService.StreamHealth`:
```csharp
public override async Task StreamHealth(
    StreamHealthRequest request,
    IServerStreamWriter<HealthStatus> responseStream,
    ServerCallContext context)
{
    // Send immediate snapshot
    await responseStream.WriteAsync(_healthReporter.GetCurrentHealth(), context.CancellationToken);

    // Then stream updates
    var ct = context.CancellationToken;
    await foreach (var status in _healthReporter.Subscribe(ct))
    {
        try { await responseStream.WriteAsync(status, ct); }
        catch (OperationCanceledException) { break; }
        catch { break; }
    }
}
```

**Edge cases**:
- The immediate snapshot on subscribe ensures the VSIX receives state immediately
  without waiting up to 30s for the first periodic tick.
- `HealthState` is `Healthy` in Phase 7. In a production hardening pass, add real
  checks: disk space, state file accessibility, vsdbg binary integrity.
- `Process.GetCurrentProcess()` allocates a new `Process` object on each call.
  Cache it as a field: `private readonly Process _self = Process.GetCurrentProcess()`.

**Testing requirements**:
- Unit test: `GetCurrentHealth()` returns non-null `HealthStatus`
- Unit test: `Subscribe` receives periodic updates (mock timer)
- Integration test: `StreamHealth` sends immediate snapshot + periodic updates
- Integration test: two simultaneous `StreamHealth` clients both receive updates

**Definition of done**:
- [ ] `PeriodicTimer(30s)` broadcasts health to channel
- [ ] `GetCurrentHealth()` for immediate snapshot on subscribe
- [ ] `HealthStatus` includes uptime, memory, active sessions, app list
- [ ] `StreamHealth` RPC sends immediate snapshot then subscribes
- [ ] Registered as `IHostedService` in `Program.cs`

---

## P7.6 — Daemon Self-Update

**Purpose**: Implement the two-phase daemon self-update flow — VSIX uploads a new binary,
daemon verifies and atomically installs it, then systemd restarts the service with
the new binary.

**Dependencies**: P2.3, P4.3

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (implement RPCs)
- `Source/VsExtension/Provisioning/DaemonInstaller.cs` (add self-update path)

**Implementation details**:

Daemon-side RPCs:

```csharp
public override async Task<PrepareUpdateResponse> PrepareUpdate(
    IAsyncStreamReader<PrepareUpdateChunk> requestStream,
    ServerCallContext context)
{
    var newBinPath = DaemonPaths.BinPath(_options) + ".new";
    var sha256Expected = "";
    await using var fs = File.Create(newBinPath);

    await foreach (var chunk in requestStream.ReadAllAsync(context.CancellationToken))
    {
        if (chunk.HasSha256) sha256Expected = chunk.Sha256;
        await fs.WriteAsync(chunk.Data.Memory, context.CancellationToken);
    }
    await fs.FlushAsync();

    // Verify
    var actualSha256 = await ComputeSha256Async(newBinPath);
    if (!string.Equals(actualSha256, sha256Expected, StringComparison.OrdinalIgnoreCase))
    {
        File.Delete(newBinPath);
        return new PrepareUpdateResponse
            { Success = false, Error = "SHA-256 mismatch" };
    }

    Mono.Unix.Native.Syscall.chmod(newBinPath,
        FilePermissions.S_IRWXU | FilePermissions.S_IRGRP | FilePermissions.S_IXGRP);

    _logger.LogInformation("Update prepared: {Path} SHA-256 verified", newBinPath);
    return new PrepareUpdateResponse { Success = true };
}

public override Task<ApplyUpdateResponse> ApplyUpdate(
    ApplyUpdateRequest request, ServerCallContext context)
{
    var binPath    = DaemonPaths.BinPath(_options);
    var newBinPath = binPath + ".new";

    if (!File.Exists(newBinPath))
        return Task.FromResult(new ApplyUpdateResponse
            { Success = false, Error = "No prepared update found. Call PrepareUpdate first." });

    // Back up current binary
    File.Copy(binPath, binPath + ".bak", overwrite: true);

    // Atomic swap
    File.Move(newBinPath, binPath, overwrite: true);

    _logger.LogInformation("Update applied. Stopping daemon for restart...");

    // Stop the daemon — systemd will restart it with the new binary
    _ = Task.Run(async () =>
    {
        await Task.Delay(500);  // give gRPC time to send response
        _lifetime.StopApplication();
    });

    return Task.FromResult(new ApplyUpdateResponse { Success = true });
}
```

VSIX side (in `DaemonInstaller`):
```csharp
public async Task SelfUpdateAsync(
    GrpcChannel channel, IProgress<string> progress, CancellationToken ct)
{
    progress.Report("Uploading new daemon binary...");
    var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
    using var call = client.PrepareUpdate(cancellationToken: ct);

    await using var binaryStream = GetEmbeddedBinary();
    const int chunkSize = 256 * 1024;
    var buffer = new byte[chunkSize];
    int bytesRead;
    while ((bytesRead = await binaryStream.ReadAsync(buffer, ct)) > 0)
        await call.RequestStream.WriteAsync(new PrepareUpdateChunk
            { Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead) }, ct);

    // Send SHA-256 hash
    await call.RequestStream.WriteAsync(new PrepareUpdateChunk
        { Sha256 = RequiredSha256 }, ct);
    await call.RequestStream.CompleteAsync();
    var prepareResp = await call;
    if (!prepareResp.Success)
        throw new ProvisioningException($"Prepare update failed: {prepareResp.Error}");

    progress.Report("Applying update (daemon will restart)...");
    var applyResp = await client.ApplyUpdateAsync(new ApplyUpdateRequest(), cancellationToken: ct);
    if (!applyResp.Success)
        throw new ProvisioningException($"Apply update failed: {applyResp.Error}");

    // Wait for daemon to restart
    progress.Report("Waiting for daemon to restart...");
    await Task.Delay(3000, ct);  // give systemd time to restart
    var healthy = await WaitForHealthAsync(channel, TimeSpan.FromSeconds(30), ct);
    if (!healthy) throw new ProvisioningException("Daemon did not restart after update");
    progress.Report("Daemon updated and running.");
}
```

**Edge cases**:
- `StopApplication()` with a 500ms delay gives the `ApplyUpdateResponse` time to be
  sent before the server shuts down. Without the delay, the gRPC response may not
  reach the client.
- After `StopApplication()`, systemd restarts the daemon (due to `Restart=on-failure`
  in the service file). The 3-second wait in the VSIX gives systemd time to detect
  the exit and launch the new binary.
- If the upload is interrupted mid-stream, `meadow-daemon.new` is left on disk.
  `PrepareUpdate` must clean it up before writing. Check `newBinPath` existence at
  the start of `PrepareUpdate` and delete if present.

**Testing requirements**:
- Integration test: upload a new binary, verify it's installed after `ApplyUpdate`
- Integration test: SHA-256 mismatch → `PrepareUpdateResponse.Success = false`
- Integration test: after `ApplyUpdate`, daemon restarts within 10s
- Unit test: `ApplyUpdate` without `PrepareUpdate` → returns failure

**Definition of done**:
- [ ] `PrepareUpdate`: streams binary, verifies SHA-256, writes `.new` file
- [ ] `ApplyUpdate`: backs up `.bak`, moves `.new` → daemon, stops application
- [ ] 500ms delay before `StopApplication` to allow response delivery
- [ ] VSIX `SelfUpdateAsync` streams binary in 256 KB chunks
- [ ] VSIX waits for daemon restart after `ApplyUpdate`
- [ ] All integration tests pass

---

## P7.7 — Installation Scripts

**Purpose**: Create production-quality shell scripts for full offline installation,
clean uninstall, and in-place update — for users who cannot or prefer not to use the
VSIX provisioning.

**Dependencies**: None (standalone scripts, no code dependency)

**Files**:
- `scripts/install.sh`
- `scripts/uninstall.sh`
- `scripts/update.sh`
- `scripts/common.sh` (shared helpers)

**Implementation details**:

`scripts/common.sh`:
```bash
#!/usr/bin/env bash
# Shared helpers for PiDbg scripts.
set -euo pipefail

DAEMON_BIN="/opt/meadow/bin/meadow-daemon"
SERVICE_FILE="$HOME/.config/systemd/user/meadow-daemon.service"
RELEASE_URL="https://github.com/WildernessLabs/pidbg/releases/latest/download"
ARCH=$(uname -m)

check_arch() {
  if [ "$ARCH" != "aarch64" ]; then
    echo "ERROR: ARM64 (aarch64) required. Detected: $ARCH" >&2
    exit 1
  fi
}

check_root_or_user() {
  if [ "$(id -u)" -eq 0 ]; then
    echo "ERROR: Do not run as root. Run as the Pi user." >&2
    exit 1
  fi
}

service_is_active() {
  systemctl --user is-active meadow-daemon >/dev/null 2>&1
}

wait_for_service() {
  local timeout=${1:-30}
  local elapsed=0
  while ! service_is_active && [ "$elapsed" -lt "$timeout" ]; do
    sleep 1
    elapsed=$((elapsed + 1))
  done
  service_is_active
}
```

`scripts/install.sh`:
```bash
#!/usr/bin/env bash
source "$(dirname "$0")/common.sh"
check_arch
check_root_or_user

DAEMON_VERSION="${1:-latest}"
echo "==> Installing meadow-daemon $DAEMON_VERSION"

# Download binary
DOWNLOAD_URL="$RELEASE_URL/meadow-daemon"
echo "  Downloading from $DOWNLOAD_URL..."
curl -fsSL -o "$DAEMON_BIN.new" "$DOWNLOAD_URL"
chmod 755 "$DAEMON_BIN.new"
mv "$DAEMON_BIN.new" "$DAEMON_BIN"

# Install service
curl -fsSL -o "$SERVICE_FILE" "$RELEASE_URL/meadow-daemon.service"
systemctl --user daemon-reload
systemctl --user enable meadow-daemon
systemctl --user start meadow-daemon

echo "  Waiting for daemon to start..."
if wait_for_service 30; then
  echo "==> Installation complete. meadow-daemon is running."
else
  echo "ERROR: Daemon did not start within 30 seconds." >&2
  echo "       Check: journalctl --user-unit meadow-daemon -n 50" >&2
  exit 1
fi
```

`scripts/uninstall.sh`:
```bash
#!/usr/bin/env bash
source "$(dirname "$0")/common.sh"
check_root_or_user

echo "==> Uninstalling meadow-daemon"
systemctl --user stop    meadow-daemon || true
systemctl --user disable meadow-daemon || true
systemctl --user daemon-reload
pkill -u "$USER" -f vsdbg-ui || true
rm -f "$DAEMON_BIN" "$DAEMON_BIN.bak" "$SERVICE_FILE"
rm -rf /opt/meadow/vsdbg
rm -f /etc/meadow/daemon.conf
echo "==> Uninstall complete."
echo "    App data preserved at /opt/meadow/apps"
echo "    Run 'sudo rm -rf /opt/meadow' to remove everything."
```

`scripts/update.sh`:
```bash
#!/usr/bin/env bash
source "$(dirname "$0")/common.sh"
check_arch
check_root_or_user

DAEMON_VERSION="${1:-latest}"
echo "==> Updating meadow-daemon to $DAEMON_VERSION"

# Backup
cp "$DAEMON_BIN" "$DAEMON_BIN.bak" 2>/dev/null || true

# Download new binary
curl -fsSL -o "$DAEMON_BIN.new" "$RELEASE_URL/meadow-daemon"
chmod 755 "$DAEMON_BIN.new"

# Stop, swap, restart
systemctl --user stop meadow-daemon
mv "$DAEMON_BIN.new" "$DAEMON_BIN"
systemctl --user start meadow-daemon

echo "  Waiting for daemon to restart..."
if wait_for_service 30; then
  NEW_VERSION=$("$DAEMON_BIN" --version 2>/dev/null || echo "unknown")
  echo "==> Update complete. Running version: $NEW_VERSION"
  rm -f "$DAEMON_BIN.bak"
else
  echo "ERROR: Daemon did not restart. Rolling back..." >&2
  mv "$DAEMON_BIN.bak" "$DAEMON_BIN"
  systemctl --user start meadow-daemon
  exit 1
fi
```

**Edge cases**:
- All scripts must have LF line endings (enforced by `.gitattributes`).
- `common.sh` is sourced with a relative path. Scripts must be run from the
  `scripts/` directory or use `$(dirname "$0")` to resolve the path.
- `curl -fsSL` fails if the URL returns 404 (wrong version). The `-f` flag makes
  curl return exit code 22 on HTTP errors. The `set -e` propagates this as a failure.
- `update.sh` rollback: if the new daemon fails to start, the backup is restored.
  This is a best-effort rollback; it does not rollback the service file.

**Testing requirements**:
- Test: `install.sh` on a clean bootstrapped Pi installs and starts the daemon
- Test: `uninstall.sh` removes binary and service; apps preserved
- Test: `update.sh` rolls back if the new binary fails to start
- Test: all three scripts are idempotent (safe to run multiple times)
- Test: LF line endings verified with `file` command

**Definition of done**:
- [ ] `common.sh` with shared `check_arch`, `check_root_or_user`, `wait_for_service`
- [ ] `install.sh`: download, chmod, move, service enable+start
- [ ] `uninstall.sh`: stop, disable, remove binary and service; preserve apps
- [ ] `update.sh`: backup, download, stop, swap, start, rollback on failure
- [ ] All scripts use `set -euo pipefail`
- [ ] All scripts have LF line endings
- [ ] Idempotency verified

---

## P7.8 — Diagnostics Bundle Export

**Purpose**: Collect all diagnostic information from the device and local machine into a
single ZIP archive that can be attached to a GitHub issue.

**Dependencies**: P6.1, P4.3, P4.5

**Files**:
- `Source/VsExtension/Diagnostics/DiagnosticsBundleExporter.cs`

**Implementation details**:

```csharp
public sealed class DiagnosticsBundleExporter
{
    private readonly SshSession _session;
    private readonly GrpcChannel? _channel;   // null if daemon not running
    private readonly IOutputWindowService _output;

    public async Task<string> ExportAsync(CancellationToken ct)
    {
        _output.WriteLine(OutputPane.Provisioning, "Collecting diagnostics...");

        var bundle = new Dictionary<string, string>();

        // 1. detection.json
        try
        {
            var detector = new CapabilityDetector();
            var detection = await detector.DetectAsync(_session, ct);
            bundle["detection.json"] = JsonSerializer.Serialize(detection,
                new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) { bundle["detection.json"] = $"ERROR: {ex.Message}"; }

        // 2. diag.txt (standalone diag script)
        var diagScript = LoadEmbeddedScript("diag.sh");
        var (_, diagOut, _) = await _session.ExecuteAsync(
            $"bash -s <<'DIAG'\n{diagScript}\nDIAG", ct);
        bundle["diag.txt"] = diagOut;

        // 3. daemon-logs.txt (last 500 journal lines)
        var (_, logsOut, _) = await _session.ExecuteAsync(
            "journalctl --user-unit meadow-daemon -n 500 --no-pager 2>/dev/null || echo '(no logs)'",
            ct);
        bundle["daemon-logs.txt"] = logsOut;

        // 4. service-file.txt
        var (_, svcOut, _) = await _session.ExecuteAsync(
            "cat ~/.config/systemd/user/meadow-daemon.service 2>/dev/null || echo '(not found)'",
            ct);
        bundle["service-file.txt"] = svcOut;

        // 5. provision-log.jsonl (most recent provisioning log)
        var logDir  = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiDbg", "Logs");
        var logFile = Directory.GetFiles(logDir, "*.jsonl").OrderByDescending(f => f).FirstOrDefault();
        bundle["provision-log.jsonl"] = logFile is not null
            ? await File.ReadAllTextAsync(logFile, ct)
            : "(no provisioning log found)";

        // 6. device-info.json (from gRPC if available)
        if (_channel is not null)
        {
            try
            {
                var client = new MeadowDaemonService.MeadowDaemonServiceClient(_channel);
                var info   = await client.GetDeviceInfoAsync(new GetDeviceInfoRequest(), cancellationToken: ct);
                bundle["device-info.json"] = JsonSerializer.Serialize(info,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch { bundle["device-info.json"] = "(daemon not available)"; }
        }

        // Create ZIP
        var hostname  = bundle.ContainsKey("detection.json")
            ? JsonSerializer.Deserialize<DetectionResult>(bundle["detection.json"])?.Host?.Hostname ?? "unknown"
            : "unknown";
        var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var zipName   = $"pidbg-diag-{hostname}-{timestamp}.zip";
        var downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var zipPath   = Path.Combine(downloads, "Downloads", zipName);

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in bundle)
        {
            var entry = zip.CreateEntry(name);
            await using var writer = new StreamWriter(entry.Open());
            await writer.WriteAsync(content);
        }

        // Open Downloads folder
        Process.Start(new ProcessStartInfo("explorer.exe",
            Path.GetDirectoryName(zipPath)!) { UseShellExecute = true });

        _output.WriteLine(OutputPane.Provisioning, $"Bundle saved: {zipPath}");
        return zipPath;
    }
}
```

**Edge cases**:
- `bundle["detection.json"]` contains the full raw JSON. If detection fails, the error
  message is stored instead. The ZIP is always produced, even if some items failed.
- `journalctl` may not be available on non-systemd devices. The `|| echo '(no logs)'`
  fallback ensures the script does not fail.
- `daemon.conf` may contain no secrets in this implementation, but document that
  future versions should redact any sensitive values before including it.
- `ZipFile.Open` requires `System.IO.Compression.ZipFile` which is available in
  `net10.0-windows` without any additional NuGet package.

**Testing requirements**:
- Integration test: produce a bundle from a real Pi, verify all 6 files are present
- Unit test: ZIP file is created in the correct location
- Unit test: daemon not running → `device-info.json` contains "(daemon not available)"
- Manual test: open bundle in a zip viewer, verify all files are readable

**Definition of done**:
- [ ] 6 files collected: detection, diag, logs, service file, provision log, device info
- [ ] ZIP created in Downloads folder with timestamped name
- [ ] Downloads folder opened in Explorer after export
- [ ] Each file captured independently (failure on one file does not abort others)
- [ ] All integration tests pass
