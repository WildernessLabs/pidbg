# PiDbg — Error Handling Strategy

---

## 1. Error Categories

### Category 1: Configuration Errors (user fix required)
- Device not found in registry
- Invalid SSH credentials / host unreachable
- Wrong SSH port
- Pi not ARM64 or not Debian 12

**Handling:** Fail fast with clear, actionable error message. Do not retry.
Surface in VS Output window + VS Error List.

### Category 2: Transient Network Errors (retry appropriate)
- SSH connection timeout
- SFTP write timeout
- gRPC deadline exceeded
- Pi temporarily unreachable (sleep/wake, network blip)

**Handling:** Polly retry with exponential backoff. Log each retry attempt.
After max retries, surface as user-visible error.

### Category 3: Pi State Errors (potentially recoverable)
- Disk full on Pi
- vsdbg port already in use
- App already running / port conflict
- Permission denied on `/opt/pidbg/`
- SHA-256 mismatch on deployment commit

**Handling:** Return structured error code. VSIX displays specific guidance per error code.

### Category 4: Protocol Errors (developer/bug)
- gRPC response deserialization failure
- Unexpected proto field values
- Agent version incompatibility
- VSIX/Agent version mismatch

**Handling:** Log full stack trace. Surface generic "internal error" to user with
"Please report this" message and a reference ID (correlation ID from structured log).

### Category 5: VS Integration Errors
- `IVsDebugger4.LaunchDebugTargets4()` failure
- Build failure (not a PiDbg error — surfaced by VS as normal)
- VS COM object unavailable (extension lifecycle issue)

**Handling:** Log and propagate. VS will show its own error UI.

---

## 2. Exception Hierarchy

```csharp
// Base for all PiDbg-originated exceptions
public abstract class PiDbgException : Exception
{
    public ErrorCode Code { get; }
    public string? Guidance { get; }  // User-readable next step
    protected PiDbgException(ErrorCode code, string message, string? guidance = null, Exception? inner = null)
        : base(message, inner) { Code = code; Guidance = guidance; }
}

// Specific subtypes
public sealed class DeviceConnectionException : PiDbgException { }
public sealed class DeploymentException : PiDbgException { }
public sealed class VsdbgException : PiDbgException { }
public sealed class AgentCommunicationException : PiDbgException { }
public sealed class AgentVersionException : PiDbgException { }
public sealed class DebugAttachException : PiDbgException { }

public enum ErrorCode
{
    // Connection
    DeviceNotFound,
    SshConnectionFailed,
    SshAuthFailed,
    AgentNotRunning,
    AgentVersionMismatch,

    // Deployment
    DeploymentDiskFull,
    DeploymentHashMismatch,
    DeploymentTransferFailed,
    DeploymentStagingFailed,

    // vsdbg
    VsdbgNotInstalled,
    VsdbgInstallFailed,
    VsdbgLaunchFailed,
    VsdbgPortUnavailable,
    VsdbgAttachFailed,
    VsdbgStartTimeout,

    // Internal
    ProtocolError,
    UnexpectedError,
}
```

---

## 3. Retry Policies (Polly)

Defined in `PiDbg.Shared/Retry/RetryPolicies.cs`:

```csharp
// Transient network operations (SSH connect, gRPC calls)
public static readonly AsyncRetryPolicy TransientNetworkRetry = Policy
    .Handle<SshConnectionException>()
    .Or<SocketException>()
    .Or<RpcException>(ex => ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
    .WaitAndRetryAsync(
        retryCount: 3,
        sleepDurationProvider: attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)),
        // delays: 1s, 2s, 4s
        onRetry: (ex, delay, attempt, ctx) =>
            ctx.GetLogger()?.LogWarning("Retry {Attempt}/3 after {Delay}s: {Error}",
                attempt, delay.TotalSeconds, ex.Message));

// Vsdbg port wait (fast polling)
public static readonly AsyncRetryPolicy VsdbgPortWait = Policy
    .Handle<VsdbgPortNotBoundException>()
    .WaitAndRetryAsync(
        retryCount: 40,       // 40 × 250ms = 10 seconds
        sleepDurationProvider: _ => TimeSpan.FromMilliseconds(250));
```

Operations that do NOT retry:
- SHA-256 mismatch (deterministic failure, retry won't help)
- Authentication failure (retry just locks account)
- Build failure (not our error)
- Disk full (retry won't help)

---

## 4. gRPC Error Mapping

`AgentClientWrapper` translates `RpcException` to domain exceptions:

```csharp
private static PiDbgException MapRpcException(RpcException ex) => ex.StatusCode switch
{
    StatusCode.Unavailable    => new AgentCommunicationException(ErrorCode.AgentNotRunning,
        "PiDbg agent is not responding. Is it running on the Pi?",
        "Run: systemctl --user status pidbg-agent"),
    StatusCode.DeadlineExceeded => new AgentCommunicationException(ErrorCode.AgentNotRunning,
        "Agent request timed out. The Pi may be busy or unreachable."),
    StatusCode.NotFound        => new DeviceConnectionException(ErrorCode.DeviceNotFound,
        "The requested resource was not found on the Pi."),
    StatusCode.ResourceExhausted => new DeploymentException(ErrorCode.DeploymentDiskFull,
        "The Pi does not have enough disk space for this deployment."),
    StatusCode.FailedPrecondition => new VsdbgException(ErrorCode.VsdbgNotInstalled,
        "vsdbg is not installed on the Pi.", "Press F5 again — it will install automatically."),
    _ => new PiDbgInternalException(ErrorCode.UnexpectedError,
        $"Unexpected gRPC error: {ex.StatusCode}", innerException: ex),
};
```

---

## 5. User-Visible Error Display

Errors surface in three places:

### VS Output Window (always)
Every error is logged with full context:
```
[PiDbg] ERROR: Deployment failed — SHA-256 mismatch
[PiDbg]   File: MyApp.dll
[PiDbg]   Expected: abc123...
[PiDbg]   Actual:   def456...
[PiDbg]   The Pi may have received a corrupted file. Try deploying again.
[PiDbg]   Correlation ID: 7f3a2b1c
```

### VS Error List (for fatal errors)
The VSIX calls `IVsErrorList` API to add a row in the Error List for errors that prevent
debugging from starting. The row links back to the Output pane entry.

### VS message box (for errors requiring immediate attention)
Used sparingly — only for:
- First-time vsdbg install prompt
- Agent version mismatch requiring manual update
- SSH auth failure (user must fix credentials)

Pattern: `MessageDialog.ShowError(message, guidance)` — uses VS-standard dialog, not
custom WPF.

---

## 6. Agent-side Error Handling

The agent uses `ILogger` for all errors. Errors in gRPC handlers are caught at the
`AgentGrpcService` level:

```csharp
public override async Task<CommitDeploymentResponse> CommitDeployment(
    CommitDeploymentRequest request, ServerCallContext context)
{
    try
    {
        var result = await _deploymentManager.CommitDeploymentAsync(request.DeploymentId, ..., context.CancellationToken);
        return result.ToProto();
    }
    catch (HashMismatchException ex)
    {
        _logger.LogError(ex, "Deployment commit failed: hash mismatch");
        throw new RpcException(new Status(StatusCode.DataLoss,
            $"SHA-256 mismatch for {ex.FilePath}: expected {ex.Expected}, got {ex.Actual}"));
    }
    catch (DiskFullException ex)
    {
        _logger.LogError(ex, "Disk full during deployment commit");
        throw new RpcException(new Status(StatusCode.ResourceExhausted,
            $"Insufficient disk space: {ex.Available} bytes free, {ex.Required} bytes required"));
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Unexpected error in CommitDeployment");
        throw new RpcException(new Status(StatusCode.Internal,
            "Internal agent error. Check agent logs."));
    }
}
```

### Unhandled exceptions
Registered via `AppDomain.CurrentDomain.UnhandledException`:
```csharp
AppDomain.CurrentDomain.UnhandledException += (s, e) =>
{
    Log.Fatal(e.ExceptionObject as Exception, "Unhandled exception — agent terminating");
    Log.CloseAndFlush();
    // systemd will restart the service
};
```

The systemd service is configured with `Restart=on-failure` and `RestartSec=3s`.
A crash loop guard: `StartLimitIntervalSec=60; StartLimitBurst=5`.

---

## 7. Partial Failure Recovery

### Deployment interrupted mid-transfer
Agent startup scans for staging directories. If a staging directory exists with no
corresponding in-progress deployment record (VSIX disconnected during transfer), it is deleted.

### vsdbg started but VSIX never attached
The vsdbg process has a 60-second idle timeout: if no debugger connects within 60 seconds
of startup, vsdbg exits. This prevents orphaned vsdbg processes accumulating on the Pi.
The agent tracks vsdbg PIDs and can force-kill them on session cleanup.

### Debug session lost (SSH disconnect during debugging)
VS debugger will report "lost connection to remote debugger." The VSIX handles
`IVsDebuggerEvents.OnModeChange(DBGMODE.DBGMODE_Design)` and performs cleanup.
The app on the Pi continues running (vsdbg may be orphaned — cleaned up on next F5).
