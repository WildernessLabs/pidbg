# Meadow.Daemon — Deployment Manager

---

## 1. Filesystem Layout

All daemon-managed state lives under `/opt/meadow/`. The layout is fixed; paths come
from `DaemonOptions` which maps to `/etc/meadow/daemon.conf`.

```
/opt/meadow/
├── daemon/                         # Daemon binary
│   ├── meadow-daemon               # Self-contained .NET 10 binary
│   └── meadow-daemon.new           # Staged during self-update (transient)
│
├── apps/                           # AppRoot — one dir per managed app
│   └── {AppName}/
│       ├── versions/               # Numbered production deployments
│       │   ├── 000001/             # Oldest retained version
│       │   │   ├── manifest.json
│       │   │   └── <app files>
│       │   ├── 000002/
│       │   │   ├── manifest.json
│       │   │   └── <app files>
│       │   └── 000003/             # Current active version
│       │       ├── manifest.json
│       │       └── <app files>
│       ├── active -> versions/000003   # Symlink — current production
│       ├── debug/                  # Debug slot — always overwritten
│       │   ├── manifest.json
│       │   └── <app files>
│       └── staging/                # Transient — present only during deploy
│           └── <app files being uploaded>
│
├── vsdbg/                          # VsdbgRoot — vsdbg installation
│   └── {version}/
│       └── vsdbg                   # vsdbg binary
│
├── state/                          # StateRoot — persistent JSON state
│   ├── apps.json                   # App records (pids, active versions, config)
│   └── sessions.json               # Debug session records
│
└── logs/                           # LogRoot — structured JSON logs
    └── daemon-{date}.jsonl
```

### Path constants

```csharp
internal static class DaemonPaths
{
    public static string VersionsDir(string appRoot, string appName)
        => Path.Combine(appRoot, appName, "versions");

    public static string VersionDir(string appRoot, string appName, string label)
        => Path.Combine(appRoot, appName, "versions", label);

    public static string ActiveLink(string appRoot, string appName)
        => Path.Combine(appRoot, appName, "active");

    public static string DebugDir(string appRoot, string appName)
        => Path.Combine(appRoot, appName, "debug");

    public static string StagingDir(string appRoot, string appName)
        => Path.Combine(appRoot, appName, "staging");

    public static string ManifestFile(string appDir)
        => Path.Combine(appDir, "manifest.json");
}
```

---

## 2. Deployment Slots

```csharp
public enum DeploymentSlot
{
    Production = 0,   // Versioned — numbered dirs, symlink active, rollback supported
    Debug      = 1,   // Single dir — always overwritten, no version history
}
```

### Production slot
- Numbered directories: `versions/000001/`, `versions/000002/`, …
- Zero-padded 6-digit sequence number stored in `VersionStore`
- `active` symlink always points to the current version directory
- Rollback = change the symlink
- Retention enforced after each successful commit (`DeploymentRetentionCount`)

### Debug slot
- Single directory: `debug/`
- Overwritten completely on every deploy
- No manifest versioning (manifest still written for verification only)
- No rollback
- Consistent with the developer mental model: "what I just published is what's running"

---

## 3. VersionStore

`VersionStore` owns the version sequence and the mapping from app name to current state.

```csharp
internal sealed class VersionStore
{
    // Returns the next zero-padded 6-digit label, e.g. "000004"
    public Task<string> AllocateNextVersionLabelAsync(string appName, CancellationToken ct);

    // Returns the label the active symlink points to, or null if no active version
    public Task<string?> GetActiveVersionLabelAsync(string appName, CancellationToken ct);

    // Returns all retained version labels in ascending order
    public Task<IReadOnlyList<string>> ListVersionLabelsAsync(string appName, CancellationToken ct);

    // Reads the manifest for a specific version
    public Task<DeploymentManifest?> GetManifestAsync(string appName, string label, CancellationToken ct);

    // Reads the manifest for the debug slot
    public Task<DeploymentManifest?> GetDebugManifestAsync(string appName, CancellationToken ct);
}
```

Version labels are computed from the filesystem: scan `versions/` directory, parse all
6-digit names, track the highest. No separate counter file — the filesystem is the source
of truth. Allocation reads the max and increments atomically under `_deploySemaphore`.

---

## 4. StagingController

Manages the transient `staging/` directory lifecycle.

```csharp
internal sealed class StagingController
{
    // Creates (or clears) the staging directory, returns its path
    public Task<string> BeginStagingAsync(string appName, CancellationToken ct);

    // Writes a file chunk to staging. Creates subdirs as needed.
    public Task WriteChunkAsync(string appName, string relativePath,
        long offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    // Deletes the staging directory (abort path)
    public Task AbortStagingAsync(string appName, CancellationToken ct);

    // Returns the staging path for final verification/commit
    public string GetStagingPath(string appName);
}
```

`BeginStagingAsync` deletes any leftover staging dir from a prior failed deploy before
creating the new one. This handles the crash-during-staging case cleanly.

---

## 5. ManifestVerifier

Verifies that a committed deployment matches its declared manifest.

```csharp
internal sealed class ManifestVerifier
{
    // Throws ManifestVerificationException if any file is missing or has wrong hash
    public Task VerifyAsync(string deployDir, DeploymentManifest manifest,
        CancellationToken ct);
}
```

```csharp
internal sealed class DeploymentManifest
{
    public string AppName { get; init; } = "";
    public string Version { get; init; } = "";           // label or "debug"
    public DeploymentSlot Slot { get; init; }
    public string EntryPoint { get; init; } = "";        // "MyApp.dll"
    public string? StartupArgs { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = ImmutableDictionary<string, string>.Empty;
    public IReadOnlyList<ManifestEntry> Files { get; init; }
        = ImmutableList<ManifestEntry>.Empty;
    public DateTimeOffset DeployedAt { get; init; }
}

internal sealed class ManifestEntry
{
    public string RelativePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
}
```

Verification is parallel (`Parallel.ForEachAsync`) with degree = `Environment.ProcessorCount`.
The Pi has 4 cores; SHA-256 on 64-bit ARM is hardware-accelerated via `System.Security.Cryptography`.

---

## 6. DeploymentManager

Orchestrates the full deployment lifecycle. One `SemaphoreSlim(1)` per app name guards
concurrent deploys to the same app.

```csharp
internal sealed class DeploymentManager
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim>
        _appLocks = new();
    private readonly VersionStore _versionStore;
    private readonly StagingController _staging;
    private readonly ManifestVerifier _verifier;
    private readonly DaemonOptions _opts;
    private readonly ILogger<DeploymentManager> _log;

    // Called by gRPC BeginDeployment
    public Task<BeginDeploymentResult> BeginDeploymentAsync(
        string appName, DeploymentSlot slot,
        IReadOnlyList<FileEntry> expectedFiles,
        CancellationToken ct);

    // Called by gRPC UploadChunk (streaming)
    public Task WriteChunkAsync(string appName,
        string relativePath, long offset,
        ReadOnlyMemory<byte> data, CancellationToken ct);

    // Called by gRPC CommitDeployment
    public Task<CommitResult> CommitDeploymentAsync(
        string appName, DeploymentSlot slot,
        DeploymentManifest manifest, CancellationToken ct);

    // Called by gRPC AbortDeployment
    public Task AbortDeploymentAsync(string appName, CancellationToken ct);

    // Called by gRPC ListDeployments
    public Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(
        string appName, CancellationToken ct);

    // Called by gRPC SetActiveVersion (rollback)
    public Task SetActiveVersionAsync(string appName, string label, CancellationToken ct);

    // Called by gRPC DeleteVersion
    public Task DeleteVersionAsync(string appName, string label, CancellationToken ct);

    // Called by gRPC PruneDeployments
    public Task<int> PruneDeploymentsAsync(string appName, int keepCount, CancellationToken ct);
}
```

---

## 7. Deployment Lifecycle (Production Slot)

```
BeginDeployment
  └── Acquire _appLocks[appName] (SemaphoreSlim)
  └── StagingController.BeginStagingAsync()   ← clears any leftover staging
  └── VersionStore.AllocateNextVersionLabelAsync()
  └── Return (deploymentId, stagingPath, label)

UploadChunk (streaming RPC — many calls)
  └── StagingController.WriteChunkAsync()     ← writes bytes to staging/

CommitDeployment
  └── ManifestVerifier.VerifyAsync(stagingPath, manifest)   ← all SHA-256s checked
  └── targetDir = versions/{label}/
  └── Directory.Move(stagingPath, targetDir)                ← ATOMIC on ext4
  └── File.WriteAllTextAsync(targetDir/manifest.json, ...)
  └── ln -sfn versions/{label} active                       ← ATOMIC symlink swap
      (via Mono.Unix.Native.Syscall.symlink + rename trick)
  └── StateStore.UpdateAppRecordAsync(appName, label)
  └── PruneDeploymentsAsync(keep = DeploymentRetentionCount)
  └── Release _appLocks[appName]

AbortDeployment (called on timeout / client disconnect)
  └── StagingController.AbortStagingAsync()   ← delete staging/
  └── Release _appLocks[appName]
```

### Atomic symlink update

Linux does not have an atomic symlink replace syscall. The standard pattern:

```csharp
// Creates a new symlink at a temp name, then renames it over the old one.
// rename(2) is atomic on the same filesystem.
private static void UpdateSymlink(string linkPath, string targetRelative)
{
    var tmpLink = linkPath + ".new";
    if (File.Exists(tmpLink)) File.Delete(tmpLink);

    // Create symlink: active.new -> versions/000003
    Syscall.symlink(targetRelative, tmpLink);

    // Atomic rename: active.new -> active
    Syscall.rename(tmpLink, linkPath);
}
```

`Mono.Posix.NETStandard` provides `Syscall` on Linux. This is a Linux-only deployment
path — no Windows fallback needed (daemon runs on the Pi).

---

## 8. Deployment Lifecycle (Debug Slot)

The debug slot is simpler — no versioning, no symlink:

```
BeginDeployment (slot=Debug)
  └── Acquire _appLocks[appName]
  └── StagingController.BeginStagingAsync()   ← clears staging/

UploadChunk → same as production

CommitDeployment (slot=Debug)
  └── ManifestVerifier.VerifyAsync(stagingPath, manifest)
  └── debugDir = apps/{appName}/debug/
  └── if Directory.Exists(debugDir) → Directory.Delete(debugDir, recursive=true)
  └── Directory.Move(stagingPath, debugDir)    ← ATOMIC on ext4 (same volume)
  └── File.WriteAllTextAsync(debugDir/manifest.json, ...)
  └── StateStore.UpdateAppDebugManifestAsync(appName)
  └── Release _appLocks[appName]
```

No pruning, no retention, no symlink. The debug dir is always the current deployment.

---

## 9. Rollback Strategy

Rollback is available for production deployments only. It is a symlink update:

```
SetActiveVersion(appName, label)
  └── Acquire _appLocks[appName]
  └── Verify versions/{label}/ exists
  └── Verify versions/{label}/manifest.json is readable
  └── UpdateSymlink(active, versions/{label})
  └── StateStore.UpdateAppRecordAsync(appName, label)
  └── Release _appLocks[appName]
```

**Important**: `SetActiveVersion` does NOT restart the app. The caller (gRPC client) is
responsible for stopping the app before rollback and restarting it after. This separation
of concerns keeps the operation atomic and auditable.

The gRPC `SetActiveVersionRequest` includes an `auto_restart` flag (default false) that
triggers `ProcessManager.RestartApplicationAsync` as a convenience — but only after the
symlink swap completes successfully.

---

## 10. Cleanup and Retention

After every successful production deploy, `PruneDeploymentsAsync` is called automatically.

```csharp
public async Task<int> PruneDeploymentsAsync(
    string appName, int keepCount, CancellationToken ct)
{
    var labels = await _versionStore.ListVersionLabelsAsync(appName, ct);
    var active = await _versionStore.GetActiveVersionLabelAsync(appName, ct);

    // Never delete the active version, even if keepCount would exclude it
    var candidates = labels
        .Where(l => l != active)
        .OrderBy(l => l)           // ascending → oldest first
        .SkipLast(keepCount - 1)   // keep the N-1 newest non-active versions
        .ToList();

    foreach (var label in candidates)
    {
        var dir = DaemonPaths.VersionDir(_opts.AppRoot, appName, label);
        Directory.Delete(dir, recursive: true);
        _log.LogInformation("Pruned deployment {App}/{Label}", appName, label);
    }

    return candidates.Count;
}
```

The `keepCount` default is `DeploymentRetentionCount` from config (default 3). The active
version is always preserved regardless of count. If active is the only version, nothing
is deleted.

---

## 11. Power-Loss Resilience

If the daemon dies during a deploy:

| Interrupted at | Recovery |
|---|---|
| During `UploadChunk` | Staging dir has partial files. Next `BeginDeployment` calls `BeginStagingAsync` which deletes and recreates staging. No orphan. |
| After `Directory.Move(staging→versions/N)` but before symlink swap | Version dir exists but is not active. Next startup's `ListVersionLabelsAsync` will see it. It can be promoted (SetActiveVersion) or deleted (DeleteVersion). No automatic action. |
| After symlink swap, before `StateStore.Update` | Active dir is correct. State store may have stale `activeVersion`. Startup reconciliation reads the symlink target to resolve the correct label and heals the state file. |
| During `Directory.Delete(debug)` on debug slot commit | Partial delete leaves debug/ in inconsistent state. `BeginStagingAsync` clears staging; next commit will `Directory.Delete` the partial debug/ and move staging in. |

Startup healing for the symlink→state discrepancy:

```csharp
// In ProcessMonitorService.ReconcileStateAsync():
var symlinkTarget = new FileInfo(activeLink).LinkTarget; // e.g. "versions/000003"
var labelFromLink = Path.GetFileName(symlinkTarget);     // "000003"
if (record.ActiveVersion != labelFromLink)
{
    _log.LogWarning("State/symlink mismatch for {App}: healing {Stale} → {Actual}",
        appName, record.ActiveVersion, labelFromLink);
    record = record with { ActiveVersion = labelFromLink };
    await _stateStore.UpdateAppRecordAsync(record, ct);
}
```

---

## 12. BeginDeploymentResult

The gRPC `BeginDeployment` response carries the staging path so the client knows where
files land (for diagnostics only — the client uses SFTP to upload):

```csharp
public sealed class BeginDeploymentResult
{
    public string DeploymentId { get; init; } = "";   // Correlation ID for this deploy
    public string StagingPath  { get; init; } = "";   // Absolute path on Pi
    public string VersionLabel { get; init; } = "";   // "000004" or "debug"
}
```

`DeploymentId` is a short random hex string (`Guid.NewGuid().ToString("N")[..12]`) used
to correlate upload chunks and the commit call. It is validated on `CommitDeployment` —
mismatched ID returns `Status.FailedPrecondition`.
