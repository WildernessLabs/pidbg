# Meadow.Daemon — Infrastructure

Covers: health monitoring, logging, self-update, concurrency model, state management,
resilience, security, authentication, and the systemd unit file.

---

## 1. Health Monitoring

### HealthReporterService

Background hosted service that publishes periodic health snapshots.

```csharp
internal sealed class HealthReporterService : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var snapshot = await BuildSnapshotAsync(stoppingToken);
            _channel.Writer.TryWrite(snapshot);   // drop oldest if full (bounded 10)
        }
    }

    private async Task<HealthStatus> BuildSnapshotAsync(CancellationToken ct)
    {
        var uptime = DateTimeOffset.UtcNow - _startedAt;
        var mem = GC.GetTotalMemory(forceFullCollection: false);

        // Disk free on AppRoot volume
        var driveInfo = new DriveInfo(Path.GetPathRoot(_opts.AppRoot)!);
        var diskFreeMb = (int)(driveInfo.AvailableFreeSpace / 1_048_576);

        // Per-app status
        var appStatuses = await _processManager.GetAllStatusesAsync(ct);

        return new HealthStatus
        {
            State = HealthState.Healthy,
            DaemonVersion = _version,
            UptimeSeconds = (long)uptime.TotalSeconds,
            MemoryBytes = mem,
            DiskFreeMb = diskFreeMb,
            Apps = { appStatuses },
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
    }
}
```

### StreamHealth gRPC handler

```csharp
public override async Task StreamHealth(
    StreamHealthRequest request,
    IServerStreamWriter<HealthStatus> responseStream,
    ServerCallContext context)
{
    // Send an immediate snapshot first, then subscribe to the channel
    var snapshot = await _healthService.GetCurrentSnapshotAsync(context.CancellationToken);
    await responseStream.WriteAsync(snapshot, context.CancellationToken);

    await foreach (var status in _healthChannel.Reader.ReadAllAsync(context.CancellationToken))
        await responseStream.WriteAsync(status, context.CancellationToken);
}
```

---

## 2. Logging Strategy

### Structured JSON to systemd journal

The daemon logs structured JSON to stdout/stderr, which systemd captures and routes to
the journal. This integrates with `journalctl --user -u meadow-daemon -o json`.

```csharp
// In Program.cs:
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(opts =>
{
    opts.IncludeScopes = true;
    opts.TimestampFormat = "O";
    opts.UseUtcTimestamp = true;
    opts.JsonWriterOptions = new JsonWriterOptions { Indented = false };
});
```

Each log entry includes: `Timestamp`, `Level`, `EventId`, `SourceContext`, `Message`,
`Exception`, and any structured properties pushed via `LogContext`.

### Log levels

| Level | Usage |
|---|---|
| `Trace` | Protocol-level: individual gRPC calls, file write offsets |
| `Debug` | Lifecycle transitions: process starts, session begins |
| `Information` | Significant events: deploy committed, session started, app exited |
| `Warning` | Unexpected but recoverable: process died, stale PID found, symlink healed |
| `Error` | Operation failed: deploy aborted, vsdbg failed to start, SHA-256 mismatch |
| `Critical` | Daemon cannot continue: state store unwritable, port bind failed |

### Log streaming gRPC

The `StreamLogs` RPC delivers live log entries to the VSIX Log Viewer:

```csharp
internal sealed class LogEventChannel
{
    // Bounded at 1000 entries; DropOldest when full
    private readonly Channel<LogEvent> _channel =
        Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
}
```

A custom `ILoggerProvider` feeds all log entries into `LogEventChannel`. The gRPC
`StreamLogs` handler reads from this channel.

---

## 3. Self-Update Strategy

Self-update proceeds in two phases to avoid race conditions with the running binary.

### Phase 1: Prepare (client uploads new binary)

```
PrepareUpdate(newVersion, sha256, sizeBytes)
  └── Verify: newVersion != currentVersion
  └── Create path: /opt/meadow/daemon/meadow-daemon.new
  └── Return: ready=true, uploadPath="/opt/meadow/daemon/meadow-daemon.new"
```

The VSIX uploads the new binary via SFTP to `uploadPath`.

### Phase 2: Apply

```
ApplyUpdate()
  └── Verify SHA-256 of meadow-daemon.new matches PrepareUpdate.sha256
  └── chmod +x meadow-daemon.new
  └── Write /opt/meadow/daemon/meadow-daemon (atomic: rename meadow-daemon.new → meadow-daemon)
  └── Log: "Self-update applied — exiting for systemd restart"
  └── IHostApplicationLifetime.StopApplication()
```

The daemon exits. `Restart=on-success` in the systemd unit causes systemd to restart
it immediately with the new binary.

The VSIX polls `PingAsync` every 2 seconds (30-second timeout) waiting for the new
version to respond.

### Rollback safety

If the new binary fails to start (crashes immediately), systemd's `StartLimitBurst` /
`StartLimitIntervalSec` stops the restart loop. The old binary is gone. Recovery requires
manual SFTP upload of a known-good binary. This is an acceptable risk — self-update is
explicit and operator-initiated, not automatic.

---

## 4. Concurrency Model (Complete Reference)

| Resource | Guard | Reason |
|---|---|---|
| Per-app deployment | `SemaphoreSlim(1)` keyed by appName | One deploy at a time per app |
| Deployment list | `ConcurrentDictionary<string, AppRecord>` | Multiple apps concurrently |
| Per-app process start/stop | `SemaphoreSlim(1)` keyed by appName | Prevent concurrent start+stop |
| vsdbg install | `SemaphoreSlim(1)` global | One install at a time |
| State persistence (apps.json) | `SemaphoreSlim(1)` global | Serialize atomic file writes |
| State persistence (sessions.json) | Same global semaphore as above | Shared lock is fine (fast ops) |
| Log event channel | `Channel<LogEvent>` bounded 1000 | Drop-oldest; one writer (logger provider) |
| Process output | `Channel<OutputLine>` per process, bounded 2000 | Drop-oldest; fan-out via subscriber list |
| Health snapshot channel | `Channel<HealthStatus>` bounded 10 | Drop-oldest; one writer (HealthReporter) |
| Debug session registry | `ConcurrentDictionary<string, DebugSessionRecord>` | Multiple sessions |
| Subscriber list (output fan-out) | `SemaphoreSlim(1)` inside ProcessOutputBroadcaster | Protects List mutation |

No `lock` statements anywhere. All synchronization is via `SemaphoreSlim` (async-compatible)
or lock-free concurrent types.

---

## 5. StateStore

Persists `apps.json` and `sessions.json` using atomic write (write-temp → `rename`).

```csharp
internal sealed class StateStore
{
    private readonly SemaphoreSlim _lock = new(1);
    private readonly string _stateRoot;

    public async Task WriteAppsAsync(IEnumerable<AppRecord> apps, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(
                new { apps = apps.ToArray() }, _jsonOpts);
            await AtomicWriteAsync(
                Path.Combine(_stateRoot, "apps.json"), json, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task WriteSessionsAsync(
        IEnumerable<DebugSessionRecord> sessions, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var json = JsonSerializer.Serialize(
                new { sessions = sessions.ToArray() }, _jsonOpts);
            await AtomicWriteAsync(
                Path.Combine(_stateRoot, "sessions.json"), json, ct);
        }
        finally { _lock.Release(); }
    }

    private static async Task AtomicWriteAsync(
        string path, string content, CancellationToken ct)
    {
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, content, Encoding.UTF8, ct);
        File.Move(tmp, path, overwrite: true);
        // File.Move with overwrite=true calls rename(2) on Linux — atomic
    }
}
```

---

## 6. Resilience

### gRPC call failures (client-side, in VSIX)

Handled by the VSIX via Polly retry policy (not daemon-side). The daemon makes no
outbound gRPC calls.

### Disk write failures

If `StateStore` fails to write (disk full, permission error), the error is logged at
`Critical` level. The daemon continues running — in-memory state remains correct; only
persistence fails. The next successful write will overwrite with correct state.

### Port bind failure

If Kestrel cannot bind gRPC port 50051 on startup (another process is using it), the
daemon exits immediately with exit code 1. systemd will retry per `RestartSec` config.
This is the correct behavior — a second daemon instance must not run.

### App crash loop detection

If an app restarts more than 5 times in 60 seconds, `ProcessMonitorService` disables
auto-restart and sets state to `Failed`:

```csharp
private readonly Dictionary<string, (int count, DateTimeOffset window)> _restartCounts = new();

private bool ShouldAutoRestart(string appName)
{
    var now = DateTimeOffset.UtcNow;
    if (!_restartCounts.TryGetValue(appName, out var entry)
        || now - entry.window > TimeSpan.FromSeconds(60))
    {
        _restartCounts[appName] = (1, now);
        return true;
    }
    if (entry.count >= 5)
    {
        _log.LogError(
            "App {App} restarted {N} times in 60s — disabling auto-restart",
            appName, entry.count);
        return false;
    }
    _restartCounts[appName] = (entry.count + 1, entry.window);
    return true;
}
```

---

## 7. Security Model

### Network exposure

- gRPC binds exclusively to `127.0.0.1:50051` — no external network exposure
- REST compat binds to `127.0.0.1:5000` — no external network exposure
- vsdbg binds to `127.0.0.1:{port}` — no external network exposure
- All external access routes through the SSH session established by the VSIX

### Authentication (gRPC)

The gRPC service accepts all connections on the loopback interface. Authentication is
enforced at the SSH layer — only a client who has successfully authenticated with the Pi
via SSH can establish the port forwarding that reaches the gRPC port.

There is no gRPC-level authentication (no TLS, no token). The network-layer isolation
(127.0.0.1 only) is the security boundary.

**Rationale**: Adding gRPC-level auth would require provisioning and rotating certificates
on each device. The SSH key (already required for deployment) provides equivalent
authentication strength with zero additional complexity.

### Filesystem permissions

The daemon runs as the pi user's systemd user service (not root). The `/opt/meadow/`
tree is owned by the pi user. The daemon has no elevated privileges.

`vsdbg --attach` requires permission to attach to a process. Since both the daemon and
the managed app run as the same user, `ptrace` works without any capability grants on
standard Debian 12 (`kernel.yama.ptrace_scope = 1` allows same-UID attach).

### OTA security (Meadow cloud path)

Cloud OTA updates are authenticated via MQTT + JWT (handled by `OtaUpdateService`).
The JWT validation is delegated to `CloudAuthClient`, which uses the device's RSA SSH
identity key to sign the auth request. This is the existing Meadow.Daemon behavior,
preserved unchanged.

---

## 8. Authentication — Device Identity

The device's SSH public key (at `~/.ssh/id_rsa.pub` or `~/.ssh/id_ed25519.pub`) serves
as its identity. This is the same key used for SSH authentication from the VSIX.

For Meadow cloud OTA, `CloudAuthClient` signs auth challenges with the private key using
`System.Security.Cryptography.RSA` (loaded from the PEM at the expected path). The cloud
verifies the signature using the registered public key.

No additional credentials are required. Provisioning a device = registering its SSH
public key with the Meadow cloud.

---

## 9. systemd Unit File

```ini
# meadow-daemon.service.template
# Installed to: ~/.config/systemd/user/meadow-daemon.service
# Enabled with: systemctl --user enable meadow-daemon
# Started with: systemctl --user start meadow-daemon

[Unit]
Description=Meadow Daemon — OTA and Remote Debug Service
Documentation=https://github.com/WildernessLabs/pidbg
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
NotifyAccess=main

ExecStart=@@DAEMON_BIN@@ @@EXTRA_ARGS@@
WorkingDirectory=@@INSTALL_DIR@@

# Restart on unexpected exit; don't restart on explicit systemctl stop (exit 0)
Restart=on-failure
RestartSec=3s
StartLimitBurst=5
StartLimitIntervalSec=60s

# Log to journal (structured JSON from the daemon)
StandardOutput=journal
StandardError=journal
SyslogIdentifier=meadow-daemon

# Environment
Environment=DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
Environment=DOTNET_USE_POLLING_FILE_WATCHER=false

# Allow the daemon to send systemd notification
Environment=NOTIFY_SOCKET=@@NOTIFY_SOCKET@@

[Install]
WantedBy=default.target
```

Placeholders `@@DAEMON_BIN@@`, `@@INSTALL_DIR@@`, and `@@EXTRA_ARGS@@` are replaced by
`install.sh` during provisioning.

`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` avoids ICU data errors on minimal Raspberry
Pi OS images where `libicu` may not be installed.

### sd_notify integration

`UseSystemd()` from `Microsoft.Extensions.Hosting.Systemd` handles the `sd_notify`
protocol automatically:

- `READY=1` sent after all `IHostedService.StartAsync` complete
- `STOPPING=1` sent when `IHostApplicationLifetime.ApplicationStopping` fires
- `WATCHDOG=1` sent periodically if `WatchdogSec` is configured (optional; not enabled
  by default)

The daemon does not need to call `sd_notify` directly — the hosting package does it.
