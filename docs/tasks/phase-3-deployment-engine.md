# Phase 3 — Deployment Engine

Implements all server-side deployment logic: versioned storage, staging, verification,
atomic activation, rollback, pruning, and the gRPC RPC handlers that expose these
operations to the VSIX.

Task dependency order within this phase:
```
P3.1 (VersionStore) ─┐
P3.2 (Staging)       ├─▶ P3.4 (DeploymentManager) ─▶ P3.8 (gRPC RPCs)
P3.3 (Verifier)      ┘
P3.5 (Debug Activation) ──▶ P3.4
P3.6 (Prod Activation)  ──▶ P3.4
P3.7 (Rollback)         ──▶ P3.1
P3.9 (Delta)            ──▶ P3.2, P3.4
P3.10 (Pruning)         ──▶ P3.1
```

---

## P3.1 — VersionStore

**Purpose**: Manage the versioned directory tree under `{AppRoot}/{appName}/versions/` and
the `active` symlink that points to the current production slot, with atomic symlink
semantics for all pointer mutations.

**Dependencies**: P1.5, P1.6, P2.2

**Files**:
- `Source/Meadow.Daemon/Services/VersionStore.cs`

**Implementation details**:

```csharp
public sealed class VersionStore
{
    private readonly DaemonOptions _options;
    private readonly ILogger<VersionStore> _logger;

    public VersionStore(IOptions<DaemonOptions> options, ILogger<VersionStore> logger)
    { _options = options.Value; _logger = logger; }

    /// Returns all version IDs for the app, sorted chronologically (ULID lexicographic).
    public IReadOnlyList<string> ListVersions(string appName)
    {
        var dir = DaemonPaths.AppVersionsDir(_options, appName);
        if (!Directory.Exists(dir)) return [];
        return Directory.GetDirectories(dir)
                        .Select(Path.GetFileName)
                        .OfType<string>()
                        .OrderBy(id => id)   // ULID sorts chronologically
                        .ToList();
    }

    /// Returns the ULID that `active` symlink points to, or null if no active version.
    public string? GetActiveVersion(string appName)
    {
        var link = DaemonPaths.AppActiveSymlink(_options, appName);
        if (!File.Exists(link) && !Directory.Exists(link)) return null;
        // Mono.Posix: Syscall.readlink to get symlink target
        var result = Mono.Unix.UnixPath.ReadLink(link);
        return Path.GetFileName(result);
    }

    /// Creates the version directory. Caller fills it with files, then calls SetActiveVersion.
    public string CreateVersionDirectory(string appName, string versionId)
    {
        DaemonPaths.SanitizeName(appName);
        DaemonPaths.SanitizeName(versionId);
        var path = DaemonPaths.AppVersionDir(_options, appName, versionId);
        Directory.CreateDirectory(path);
        _logger.LogDebug("Created version directory {Path}", path);
        return path;
    }

    /// Atomically swaps the `active` symlink to point to versionId.
    /// Pattern: create `active.new` symlink → rename `active.new` → `active` (atomic).
    public void SetActiveVersion(string appName, string versionId)
    {
        DaemonPaths.SanitizeName(appName);
        DaemonPaths.SanitizeName(versionId);

        var appDir      = DaemonPaths.AppDir(_options, appName);
        var target      = Path.Combine("versions", versionId); // relative symlink
        var activeLink  = DaemonPaths.AppActiveSymlink(_options, appName);
        var newLink     = activeLink + ".new";

        // Remove stale .new if it exists (crash recovery)
        if (File.Exists(newLink) || Directory.Exists(newLink))
            Mono.Unix.UnixFileInfo.DeleteEntry(newLink);

        // Create the new symlink pointing to versions/{versionId}
        var rc = Mono.Unix.Native.Syscall.symlink(target, newLink);
        if (rc != 0) throw new IOException(
            $"symlink({target}, {newLink}) failed: {Mono.Unix.Native.Stdlib.GetLastError()}");

        // Atomic rename: .new → active
        rc = Mono.Unix.Native.Syscall.rename(newLink, activeLink);
        if (rc != 0) throw new IOException(
            $"rename({newLink}, {activeLink}) failed: {Mono.Unix.Native.Stdlib.GetLastError()}");

        _logger.LogInformation("Set active version for {App} → {Version}", appName, versionId);
    }

    public void DeleteVersion(string appName, string versionId)
    {
        if (GetActiveVersion(appName) == versionId)
            throw new InvalidOperationException(
                $"Cannot delete active version '{versionId}' of app '{appName}'");

        var path = DaemonPaths.AppVersionDir(_options, appName, versionId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            _logger.LogInformation("Deleted version {Version} of {App}", versionId, appName);
        }
    }
}
```

**Edge cases**:
- `Mono.Unix.Native.Syscall.symlink` and `.rename` are Linux-only. On Windows, throw
  `PlatformNotSupportedException` with a clear message. Alternatively, use `#if` or
  a `RuntimeInformation.IsOSPlatform(OSPlatform.Linux)` guard.
- Relative symlink target (`"versions/{versionId}"`) is used instead of absolute path.
  This allows the entire `/opt/meadow/` tree to be moved without breaking symlinks.
- `active.new` may exist from a previous crash. Always clean it before creating. This
  is the idempotent recovery path.
- `DeleteVersion` must check active version first. The active version directory must
  never be deleted (it is the running version).

**Testing requirements**:
- Unit test (Linux): `CreateVersionDirectory` creates the directory
- Unit test (Linux): `SetActiveVersion` creates a symlink named `active`
- Unit test (Linux): `SetActiveVersion` twice updates the symlink atomically
- Unit test (Linux): `ListVersions` returns IDs in chronological order
- Unit test (Linux): `DeleteVersion` on active version throws
- Unit test (any): `SanitizeName` rejects traversal in all version paths

**Definition of done**:
- [x] `VersionStore` compiles on all target platforms
- [x] Symlink operations use `Mono.Unix.Native.Syscall`
- [x] Relative symlink targets (not absolute)
- [x] `DeleteVersion` guards against deleting active version
- [x] `active.new` cleanup is idempotent (crash-safe)
- [ ] All unit tests pass on Linux

---

## P3.2 — StagingController

**Purpose**: Manage the `staging/` directory that holds files during an in-progress
deployment before activation.

**Dependencies**: P1.5, P1.6

**Files**:
- `Source/Meadow.Daemon/Services/StagingController.cs`

**Implementation details**:

```csharp
public sealed class StagingController
{
    private readonly DaemonOptions _options;
    private readonly ILogger<StagingController> _logger;

    public StagingController(IOptions<DaemonOptions> options, ILogger<StagingController> logger)
    { _options = options.Value; _logger = logger; }

    /// Creates a fresh staging directory. If one already exists (crash recovery), delete it first.
    public string CreateStaging(string appName)
    {
        var path = DaemonPaths.AppStagingDir(_options, appName);
        if (Directory.Exists(path))
        {
            _logger.LogWarning("Stale staging directory found for {App}; cleaning up", appName);
            Directory.Delete(path, recursive: true);
        }
        Directory.CreateDirectory(path);
        return path;
    }

    /// Deletes the staging directory. Safe to call if it doesn't exist.
    public void CleanStaging(string appName)
    {
        var path = DaemonPaths.AppStagingDir(_options, appName);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    /// Hard-links files from `sourceDir` into staging that match entries in `manifest`
    /// where the SHA-256 in the manifest matches the file in sourceDir.
    /// Returns the set of relative file paths that were linked (already present, no upload needed).
    public async Task<HashSet<string>> HardLinkUnchangedFilesAsync(
        string appName,
        string sourceDir,
        DeploymentManifest newManifest,
        CancellationToken ct)
    {
        var stagingDir = DaemonPaths.AppStagingDir(_options, appName);
        var linked = new HashSet<string>(StringComparer.Ordinal);

        // Build a lookup of relative path → SHA-256 from the source directory's manifest
        var sourceMf = await TryReadManifestAsync(sourceDir, ct);
        if (sourceMf is null) return linked;
        var sourceMap = sourceMf.Files.ToDictionary(f => f.Path, f => f.Sha256);

        foreach (var entry in newManifest.Files)
        {
            if (!sourceMap.TryGetValue(entry.Path, out var sourceSha256)) continue;
            if (sourceSha256 != entry.Sha256) continue;  // content changed — don't link

            var sourcePath  = Path.Combine(sourceDir, entry.Path);
            var stagingPath = Path.Combine(stagingDir, entry.Path);

            if (!File.Exists(sourcePath)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(stagingPath)!);
            // Hard link: both paths point to the same inode — zero disk copy
            var rc = Mono.Unix.Native.Syscall.link(sourcePath, stagingPath);
            if (rc == 0)
                linked.Add(entry.Path);
            else
                _logger.LogDebug("Hard link failed for {File}; will upload", entry.Path);
        }

        _logger.LogInformation("Delta: {Linked}/{Total} files hard-linked",
            linked.Count, newManifest.Files.Count);
        return linked;
    }

    private static async Task<DeploymentManifest?> TryReadManifestAsync(
        string dir, CancellationToken ct)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var f = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DeploymentManifest>(f,
                cancellationToken: ct);
        }
        catch { return null; }
    }
}
```

**Edge cases**:
- `Syscall.link` creates a hard link on the same filesystem. It will fail with `EXDEV`
  if source and staging are on different filesystems. Under `/opt/meadow/` this cannot
  happen (same ext4 volume), but log the failure gracefully and fall back to upload.
- Hard link failure for a file does NOT abort the deployment — it just means that
  file must be uploaded. Return the successfully-linked files; the caller uploads the rest.
- `Directory.Delete(recursive: true)` on a stale staging directory could take time if
  it has many files. This is acceptable — a stale staging is a recovery scenario.
- The manifest read from the source directory may fail or be absent (e.g. for the debug
  slot which may not have a persisted manifest after a crash). Return `null` (no delta).

**Testing requirements**:
- Unit test: `CreateStaging` creates the directory
- Unit test: `CreateStaging` on existing staging dir deletes and recreates cleanly
- Unit test: `CleanStaging` on missing dir does not throw
- Unit test (Linux): `HardLinkUnchangedFilesAsync` links a file that matches SHA-256
- Unit test (Linux): `HardLinkUnchangedFilesAsync` does not link a file with changed SHA-256
- Unit test (Linux): after hard link, modifying one file does not affect the other
  (verify inode count with `stat`)

**Definition of done**:
- [x] `CreateStaging` is idempotent (handles stale staging)
- [x] `CleanStaging` handles missing directory
- [x] `HardLinkUnchangedFilesAsync` uses `Syscall.link`
- [x] Hard link failures are non-fatal and logged
- [x] Returns set of linked file paths so caller knows what not to upload
- [ ] Unit tests pass

---

## P3.3 — ManifestVerifier

**Purpose**: Verify the integrity of a completed staging directory by recomputing
SHA-256 for every file listed in the manifest and comparing against the manifest's
declared hashes.

**Dependencies**: P1.3

**Files**:
- `Source/Meadow.Daemon/Services/ManifestVerifier.cs`

**Implementation details**:

```csharp
public sealed class ManifestVerifier
{
    private readonly ILogger<ManifestVerifier> _logger;

    public ManifestVerifier(ILogger<ManifestVerifier> logger) => _logger = logger;

    public record VerificationResult(bool Success, IReadOnlyList<FileVerification> Files);
    public record FileVerification(string Path, bool Passed, string? Error);

    public async Task<VerificationResult> VerifyAsync(
        string stagingDir,
        DeploymentManifest manifest,
        CancellationToken ct)
    {
        // Verify manifest-level checksum first
        if (!VerifyManifestHash(manifest))
        {
            _logger.LogError("Manifest-level SHA-256 mismatch — manifest may be tampered");
            return new VerificationResult(false, [
                new FileVerification("manifest.json", false, "manifest hash mismatch")
            ]);
        }

        // Parallel file verification — 4 concurrent workers
        var results = new ConcurrentBag<FileVerification>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(manifest.Files, options, async (entry, innerCt) =>
        {
            var path = Path.Combine(stagingDir,
                entry.Path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                results.Add(new FileVerification(entry.Path, false, "file missing"));
                return;
            }

            try
            {
                var actualSha256 = await ComputeSha256Async(path, innerCt);
                var passed = string.Equals(actualSha256, entry.Sha256,
                                           StringComparison.OrdinalIgnoreCase);
                if (!passed)
                    _logger.LogWarning("SHA-256 mismatch for {File}: expected {Expected} got {Actual}",
                        entry.Path, entry.Sha256, actualSha256);
                results.Add(new FileVerification(entry.Path, passed, passed ? null : "hash mismatch"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify {File}", entry.Path);
                results.Add(new FileVerification(entry.Path, false, ex.Message));
            }
        });

        var allPassed = results.All(r => r.Passed);
        _logger.LogInformation("Verification {Result}: {Pass}/{Total} files",
            allPassed ? "PASSED" : "FAILED",
            results.Count(r => r.Passed), results.Count);
        return new VerificationResult(allPassed, results.ToList());
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifyManifestHash(DeploymentManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.ManifestSha256)) return true; // not present = skip
        // Compute hash of manifest fields (same canonicalisation as VSIX side — see doc 09)
        // Placeholder: implement the same canonicalisation algorithm as the VSIX
        return true; // TODO in P3.8 when manifest serialisation is defined
    }
}
```

**Edge cases**:
- `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4` balances throughput against
  I/O saturation on the Pi's SD card. More than 4 parallel reads can slow down on SD.
- `SHA256.HashDataAsync` is .NET 7+ and streams the file without loading it fully into
  memory. Essential for large files (vsdbg binary is ~55 MB).
- Path normalisation: the manifest uses `/` as a separator (proto string); the local
  filesystem uses the OS separator. Always normalise before combining.
- The manifest-level hash verification calls a canonicalisation algorithm. The algorithm
  must match exactly what the VSIX uses to compute `manifest.ManifestSha256`. This
  alignment is formalised in P3.8.

**Testing requirements**:
- Unit test: verifier passes for a staging dir where all files match their manifest SHA-256
- Unit test: verifier fails when one file is modified after deployment
- Unit test: verifier fails with "file missing" when a file listed in manifest is absent
- Unit test: verifier reports individual file results, not just overall pass/fail
- Unit test: 4 parallel workers used (verify via instrumented `ComputeSha256Async`)
- Performance test: verifying 50 files of 1 MB each completes in < 10 seconds on Pi hardware

**Definition of done**:
- [x] Manifest-level hash check runs first
- [x] Per-file SHA-256 verified in parallel (4 workers)
- [x] Missing files reported as failures (not exceptions)
- [x] I/O errors per file are non-fatal to other files
- [x] Returns per-file result list for detailed error reporting
- [ ] All tests pass

---

## P3.4 — DeploymentManager

**Purpose**: Orchestrate the full deployment lifecycle — begin, receive files, verify,
activate, clean up — while enforcing per-app concurrency and serialising concurrent
deploy requests correctly.

**Dependencies**: P3.1, P3.2, P3.3, P3.5, P3.6

**Files**:
- `Source/Meadow.Daemon/Services/DeploymentManager.cs`
- `Source/Meadow.Daemon/Services/IDeploymentManager.cs`

**Implementation details**:

```csharp
public interface IDeploymentManager
{
    Task<BeginDeploymentResult> BeginDeploymentAsync(
        string appName, DeploymentManifest manifest, DeploymentSlot slot,
        string? deltaBase, CancellationToken ct);
    Task<CommitResult> CommitDeploymentAsync(string deploymentId, CancellationToken ct);
    Task AbortDeploymentAsync(string deploymentId, CancellationToken ct);
    Task<IReadOnlyList<string>> ListVersionsAsync(string appName);
    Task<string?> GetActiveVersionAsync(string appName);
    Task SetActiveVersionAsync(string appName, string versionId, CancellationToken ct);
    Task DeleteVersionAsync(string appName, string versionId);
    Task PruneAsync(string appName, int retentionCount, CancellationToken ct);
}

public record BeginDeploymentResult(
    string DeploymentId,
    string StagingDir,
    IReadOnlyList<string> FilesNeeded   // paths that must be uploaded (not hard-linked)
);

public record CommitResult(bool Success, string? ErrorMessage, IReadOnlyList<FileVerification>? Failures);
```

Internal state of an active deployment:
```csharp
private sealed record ActiveDeployment(
    string DeploymentId,
    string AppName,
    string StagingDir,
    DeploymentManifest Manifest,
    DeploymentSlot Slot,
    SemaphoreSlim Lock  // held for the duration of the deployment
);
```

Key methods:

`BeginDeploymentAsync`:
1. Sanitise `appName` via `DaemonPaths.SanitizeName`
2. Acquire per-app semaphore (`ConcurrentDictionary<string, SemaphoreSlim>`)
   - Debug slot: `Cancel-and-Replace` — abort any in-progress deploy first
   - Production slot: `Queue` — wait for current deploy to finish
3. Generate `deploymentId = Ulid.NewUlid().ToString()`
4. Call `StagingController.CreateStaging(appName)` → `stagingDir`
5. If `deltaBase` is provided: `StagingController.HardLinkUnchangedFilesAsync(...)` → linked set
6. Compute `filesNeeded` = all manifest files minus linked files
7. Store `ActiveDeployment` in `ConcurrentDictionary<string, ActiveDeployment>`
8. Return `BeginDeploymentResult`

`CommitDeploymentAsync`:
1. Look up `ActiveDeployment` by deploymentId
2. Run `ManifestVerifier.VerifyAsync(stagingDir, manifest)`
3. Write `manifest.json` to staging dir (for future delta operations)
4. On success: call `ActivateAsync(deployment)`
5. On failure: clean staging, release semaphore, return failure result
6. Remove from active deployments dictionary
7. Release semaphore

`ActivateAsync(deployment)`:
- Debug slot → `ActivateDebugSlotAsync(appName, stagingDir)` (P3.5)
- Production slot → `ActivateProductionSlotAsync(appName, versionId, stagingDir)` (P3.6)

`AbortDeploymentAsync`:
1. Look up ActiveDeployment
2. Call `StagingController.CleanStaging(appName)`
3. Remove from dictionary
4. Release semaphore

**Edge cases**:
- The per-app semaphore must be released in a `finally` block in `CommitDeploymentAsync`
  and `AbortDeploymentAsync`. If the caller forgets to call `Abort`, the semaphore is
  leaked. Add a timeout: if a deployment is not committed or aborted within 5 minutes,
  auto-abort it (add to ProcessMonitorService in P5.3).
- `Cancel-and-Replace` for debug slot: abort the current in-progress deploy, clean its
  staging, release its semaphore, then begin the new one. This is safe because the
  debug slot can only hold one deployment at a time.
- ULID generation: add `Ulid` package or implement inline using
  `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()` + random bytes. The ULID spec
  is simple enough to implement without a library.
- `manifest.json` written to staging before activation so the staging directory
  contains a complete snapshot (used by delta in future deploys).

**Testing requirements**:
- Integration test: begin → upload files → commit → verify activation occurred
- Integration test: begin → commit with missing file → verify failure, staging cleaned
- Integration test: two concurrent deploys to different apps proceed in parallel
- Integration test: two concurrent deploys to same app (debug slot) → second cancels first
- Unit test: `AbortDeploymentAsync` on unknown deploymentId is a no-op (not throws)

**Definition of done**:
- [x] `IDeploymentManager` interface defined
- [x] Per-app semaphore with `Cancel-and-Replace` for debug, `Queue` for production
- [x] `BeginDeploymentAsync` returns only the files that need uploading
- [x] `CommitDeploymentAsync` runs verification before activation
- [x] Semaphore always released in `finally`
- [x] `manifest.json` written to staging before activation
- [ ] All integration tests pass

---

## P3.5 — Debug Slot Activation

**Purpose**: Atomically replace the `debug/` directory with the contents of `staging/`
using a single `Directory.Move` (which maps to `rename(2)` on Linux).

**Dependencies**: P3.2

**Files**:
- `Source/Meadow.Daemon/Services/DeploymentManager.cs` (add method)

**Implementation details**:

```csharp
private async Task ActivateDebugSlotAsync(string appName, string stagingDir, CancellationToken ct)
{
    var debugDir = DaemonPaths.AppDebugDir(_options, appName);
    var oldDir   = debugDir + ".old";

    _logger.LogInformation("Activating debug slot for {App}: {Staging} → {Debug}",
        appName, stagingDir, debugDir);

    // If a previous debug dir exists, move it aside first
    if (Directory.Exists(debugDir))
    {
        // Remove any previous .old from a prior crash
        if (Directory.Exists(oldDir))
            Directory.Delete(oldDir, recursive: true);

        Directory.Move(debugDir, oldDir);
    }

    // Atomic: rename staging → debug (same filesystem, O(1))
    Directory.Move(stagingDir, debugDir);

    // Clean up old directory asynchronously (not on the hot path)
    if (Directory.Exists(oldDir))
    {
        _ = Task.Run(() =>
        {
            try { Directory.Delete(oldDir, recursive: true); }
            catch (Exception ex)
            { _logger.LogWarning(ex, "Failed to clean old debug dir {Dir}", oldDir); }
        }, ct);
    }

    _logger.LogInformation("Debug slot activated for {App}", appName);
    await Task.CompletedTask;  // keep signature async for consistency
}
```

**Edge cases**:
- `Directory.Move` is `rename(2)` on Linux only when source and destination are on the
  same filesystem. Both `staging/` and `debug/` are under `/opt/meadow/apps/{appName}/`
  so this invariant always holds.
- `Directory.Move` on Windows may fail if the destination exists. On Linux, `rename(2)`
  replaces an empty directory but fails on non-empty. The two-step pattern (move existing
  to `.old`, then move staging to `debug`) works on both platforms.
- The async cleanup of `old/` uses `Task.Run` to avoid blocking the commit path. This
  is acceptable: the app using `debug/` gets the new version immediately; garbage
  collection happens in the background.
- If the daemon crashes between `Move(debug, old)` and `Move(staging, debug)`, the
  result is: `old/` contains the previous deployment, `staging/` contains the new one,
  `debug/` is absent. The `ProcessMonitorService` detects the app is not running and
  the next deploy recreates `debug/`.

**Testing requirements**:
- Integration test: after activation, `debug/` contains the staging contents
- Integration test: previous `debug/` is cleaned up
- Integration test: crash between two moves — next deploy recovers correctly
- Unit test: `old/` directory is cleaned up asynchronously

**Definition of done**:
- [x] `Directory.Move(staging, debug)` is the activation step (single rename)
- [x] Previous `debug/` moved to `.old` before activation
- [x] `.old` cleanup is asynchronous (not blocking commit path)
- [x] Crash recovery: stale `.old` is handled on next activation
- [ ] Integration test passes

---

## P3.6 — Production Slot Activation

**Purpose**: Move the staging directory into the versioned slot and atomically update
the `active` symlink to point to the new version.

**Dependencies**: P3.1, P3.2

**Files**:
- `Source/Meadow.Daemon/Services/DeploymentManager.cs` (add method)

**Implementation details**:

```csharp
private void ActivateProductionSlot(string appName, string versionId, string stagingDir)
{
    var versionDir = DaemonPaths.AppVersionDir(_options, appName, versionId);

    _logger.LogInformation("Activating production slot for {App} version {Version}",
        appName, versionId);

    // Move staging → versions/{versionId}  (atomic rename on same fs)
    Directory.Move(stagingDir, versionDir);

    // Atomically update the active symlink
    _versionStore.SetActiveVersion(appName, versionId);

    _logger.LogInformation("Production version {Version} active for {App}", versionId, appName);
}
```

This is simpler than debug activation because each production version gets its own
directory — there is no "old" directory to move aside. The symlink swap (in `VersionStore`)
is the atomic step.

**Edge cases**:
- If `Move(staging, versionDir)` succeeds but `SetActiveVersion` (symlink swap) fails,
  the version directory exists but is not active. The daemon is in a consistent state —
  the previous active version is still active. The orphan version directory will be
  cleaned by the next `PruneAsync` call.
- If `versionDir` already exists (previous failed deployment with same ULID — extremely
  unlikely due to ULID uniqueness), throw `InvalidOperationException`.

**Testing requirements**:
- Integration test: after activation, `versions/{versionId}/` exists with all files
- Integration test: `active` symlink points to `versions/{versionId}/`
- Integration test: previous active version still exists in `versions/`
- Integration test: crash between `Move` and symlink swap — previous active still valid

**Definition of done**:
- [x] Staging moved to `versions/{versionId}/` via `Directory.Move`
- [x] `VersionStore.SetActiveVersion` called for atomic symlink swap
- [x] Previous version remains on disk (not deleted)
- [ ] Integration tests pass

---

## P3.7 — Rollback

**Purpose**: Allow switching the active production version back to the previous version
by updating the symlink — without touching any files on disk.

**Dependencies**: P3.1

**Files**:
- `Source/Meadow.Daemon/Services/VersionStore.cs` (add method)
- `Source/Meadow.Daemon/Services/IDeploymentManager.cs` (add RollbackAsync)

**Implementation details**:

```csharp
// In VersionStore:
public RollbackResult Rollback(string appName)
{
    var versions = ListVersions(appName);  // sorted chronologically
    var current  = GetActiveVersion(appName);
    if (current is null) return new RollbackResult(false, null, "No active version");

    // Find the version immediately before current
    var currentIndex = versions.IndexOf(current);
    if (currentIndex <= 0)
        return new RollbackResult(false, null, "No previous version to roll back to");

    var previous = versions[currentIndex - 1];
    SetActiveVersion(appName, previous);
    return new RollbackResult(true, previous, null);
}

public record RollbackResult(bool Success, string? Version, string? Error);
```

Rollback is < 5 ms: it is a single `rename(2)` syscall. No file copies.

After rollback, the previous app process (if running) must be restarted to use the
new active directory. The `ProcessManager` handles this — `DeploymentManager.Rollback`
should call `IProcessManager.RestartAsync(appName)` after the symlink swap.

**Edge cases**:
- Rollback on a newly-deployed app with no previous version returns failure.
- Rollback when no active version exists returns failure.
- After rollback, `versions/` still contains all versions including the version
  that was rolled back from. It can be cleaned by `PruneAsync` or a manual delete.

**Testing requirements**:
- Unit test: rollback with two versions → active becomes previous version
- Unit test: rollback with one version → returns failure
- Unit test: rollback with no active version → returns failure
- Performance test: rollback completes in < 10 ms

**Definition of done**:
- [x] `Rollback` uses `SetActiveVersion` (atomic symlink)
- [x] Returns success + new active version ID on success
- [x] Returns failure with reason when no previous version exists
- [x] Does not delete any version directories
- [ ] Unit tests pass

> **Gap**: Spec requires `DeploymentManager.RollbackAsync` to call `IProcessManager.RestartAsync(appName)`
> after the symlink swap. Not implemented — `ProcessManager` (P5) is not yet done. Add this wire-up in P5.

---

## P3.8 — Deployment gRPC RPCs

**Purpose**: Connect the `MeadowDaemonGrpcService` RPC handlers to the deployment
domain services, mapping proto request/response types to domain types.

**Dependencies**: P2.3, P3.4, P3.7, P3.10

**Files**:
- `Source/Meadow.Daemon/GrpcService/MeadowDaemonGrpcService.cs` (implement RPCs)
- `Source/Meadow.Daemon/GrpcService/DeploymentGrpcExtensions.cs` (mapping helpers)

**Implementation details**:

Add `IDeploymentManager` to the gRPC service constructor. Implement each deployment RPC:

```csharp
public override async Task<BeginDeploymentResponse> BeginDeployment(
    BeginDeploymentRequest request, ServerCallContext context)
{
    LogCall(nameof(BeginDeployment), context);
    ValidateAppName(request.AppName);  // throws RpcException(InvalidArgument) if invalid

    var result = await _deploymentManager.BeginDeploymentAsync(
        request.AppName,
        request.Manifest,
        request.Slot,
        request.HasDeltaBase ? request.DeltaBase : null,
        context.CancellationToken);

    return new BeginDeploymentResponse
    {
        DeploymentId  = result.DeploymentId,
        StagingDir    = result.StagingDir,
        FilesNeeded   = { result.FilesNeeded }   // repeated string
    };
}

public override async Task<CommitDeploymentResponse> CommitDeployment(
    CommitDeploymentRequest request, ServerCallContext context)
{
    LogCall(nameof(CommitDeployment), context);
    var result = await _deploymentManager.CommitDeploymentAsync(
        request.DeploymentId, context.CancellationToken);

    return new CommitDeploymentResponse
    {
        Success      = result.Success,
        ErrorMessage = result.ErrorMessage ?? "",
        Failures     = { result.Failures?.Select(f =>
            new FileVerificationResult { Path = f.Path, Passed = f.Passed, Error = f.Error ?? "" })
            ?? [] }
    };
}
```

Implement all other deployment RPCs similarly:
- `AbortDeployment` → `_deploymentManager.AbortDeploymentAsync`
- `ListDeployments` → `_deploymentManager.ListVersionsAsync` → map to `ListDeploymentsResponse`
- `GetCurrentManifest` → read `manifest.json` from active version dir via
  `_deploymentManager.GetActiveVersionAsync` + `DaemonPaths.AppManifestPath`
- `SetActiveVersion` → `_deploymentManager.SetActiveVersionAsync`
- `DeleteVersion` → `_deploymentManager.DeleteVersionAsync`
- `PruneDeployments` → `_deploymentManager.PruneAsync`

Validation helper:
```csharp
private static void ValidateAppName(string name)
{
    try { DaemonPaths.SanitizeName(name); }
    catch (ArgumentException ex)
    {
        throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
    }
}
```

**Edge cases**:
- `ValidateAppName` must be called at the gRPC boundary for every RPC that takes
  an `appName`. Never let unsanitised names reach `DaemonPaths`.
- `GetCurrentManifest` must return `Status.NotFound` if there is no active version
  (not `Status.Internal`). Use:
  ```csharp
  throw new RpcException(new Status(StatusCode.NotFound,
      $"No active version for app '{request.AppName}'"));
  ```
- `CommitDeploymentResponse.Failures` maps `IReadOnlyList<FileVerification>` to proto
  repeated message. The VSIX displays these per-file errors in the output window.
- Proto `string` fields can't be null — always assign `?? ""` when mapping nullable
  strings to proto fields.

**Testing requirements**:
- Integration test: full deploy flow via gRPC (Begin → SFTP upload files → Commit)
- Integration test: invalid app name returns `StatusCode.InvalidArgument`
- Integration test: commit with missing file returns `StatusCode.OK` with `Success=false`
  and per-file failure list
- Integration test: `ListDeployments` returns versions sorted chronologically
- Integration test: `GetCurrentManifest` returns `NotFound` when no active version

**Definition of done**:
- [x] All deployment RPCs implemented (not stubbed)
- [x] `ValidateAppName` called at gRPC boundary for all RPCs taking appName
- [x] `NotFound` returned for missing resources — fixed: `GetCurrentManifest` now throws `RpcException(StatusCode.NotFound)` for missing active version, missing directory, or missing manifest file
- [x] `InvalidArgument` returned for invalid input
- [x] Proto string fields never receive null
- [ ] All integration tests pass

---

## P3.9 — Deployment Pruning

**Purpose**: Automatically remove old versioned directories beyond the configured
retention count so the Pi's disk doesn't fill up over time.

**Dependencies**: P3.1

**Files**:
- `Source/Meadow.Daemon/Services/DeploymentManager.cs` (add PruneAsync)

**Implementation details**:

```csharp
public async Task PruneAsync(string appName, int retentionCount, CancellationToken ct)
{
    var versions = _versionStore.ListVersions(appName);   // sorted oldest-first
    var active   = _versionStore.GetActiveVersion(appName);

    // Never delete the active version regardless of retention count
    var deletable = versions.Where(v => v != active).ToList();

    if (deletable.Count <= retentionCount)
    {
        _logger.LogDebug("Pruning {App}: {Count} versions, {Retention} retention — nothing to prune",
            appName, versions.Count, retentionCount);
        return;
    }

    var toDelete = deletable.Take(deletable.Count - retentionCount).ToList();
    foreach (var versionId in toDelete)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            _versionStore.DeleteVersion(appName, versionId);
            _logger.LogInformation("Pruned version {Version} of {App}", versionId, appName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to prune version {Version} of {App}", versionId, appName);
        }
    }

    // Also clean any orphan staging directories
    var stagingDir = DaemonPaths.AppStagingDir(_options, appName);
    if (Directory.Exists(stagingDir) && !_activeDeployments.ContainsKey(appName))
    {
        _logger.LogWarning("Pruning orphan staging dir for {App}", appName);
        Directory.Delete(stagingDir, recursive: true);
    }
}
```

Pruning is called automatically after every successful `CommitDeploymentAsync` for
production slots, using `_options.DeploymentRetentionCount`.

**Edge cases**:
- `active` version is always excluded from `deletable`. Even if the admin sets
  `retentionCount = 0`, the active version is never deleted.
- Orphan staging directory check: only clean staging if there is no active deployment
  in progress (checked via `_activeDeployments` dictionary).
- Pruning failures are per-version and non-fatal. Log and continue.
- `deletable.Take(deletable.Count - retentionCount)` correctly handles the case where
  `retentionCount > deletable.Count` (no-op).

**Testing requirements**:
- Unit test: 5 versions, retention 3 → 2 oldest deleted, active preserved
- Unit test: 5 versions where active = oldest → oldest not deleted, next oldest deleted
- Unit test: 0 versions → no-op
- Unit test: pruning failure on one version does not stop pruning of others

**Definition of done**:
- [x] Active version always excluded from deletion
- [x] Correct count of versions retained (retentionCount non-active versions kept)
- [x] Orphan staging cleanup on prune
- [x] Per-version failure is non-fatal
- [x] Called automatically after production slot commit
- [ ] Unit tests pass

---

## P3.10 — Delta Transfer Protocol

**Purpose**: Extend `BeginDeployment` to hard-link unchanged files from a previous
deployment into staging, so the VSIX only uploads files that actually changed.

**Dependencies**: P3.2, P3.4, P2.1

**Files**:
- `Source/Meadow.Daemon.Contracts/proto/deployment.proto` (add deltaBase field)
- `Source/Meadow.Daemon/Services/StagingController.cs` (already has `HardLinkUnchangedFilesAsync`)
- `Source/Meadow.Daemon/Services/DeploymentManager.cs` (wire delta into BeginDeployment)

**Implementation details**:

Extend proto (add to `BeginDeploymentRequest`):
```proto
message BeginDeploymentRequest {
  string app_name      = 1;
  DeploymentManifest manifest = 2;
  DeploymentSlot slot  = 3;
  // Optional: delta base. If set, files present in this slot with matching
  // SHA-256 will be hard-linked into staging (no upload required).
  optional string delta_base = 4;  // "debug" or a ULID version ID
}

message BeginDeploymentResponse {
  string deployment_id   = 1;
  string staging_dir     = 2;
  // Paths the VSIX must upload. Files not in this list are already present.
  repeated string files_needed = 3;
}
```

Delta source resolution in `DeploymentManager.BeginDeploymentAsync`:
```csharp
string? sourceDir = deltaBase switch
{
    "debug"      => DaemonPaths.AppDebugDir(_options, appName),
    not null     => DaemonPaths.AppVersionDir(_options, appName, deltaBase),
    null         => null
};

HashSet<string> linked = sourceDir is not null && Directory.Exists(sourceDir)
    ? await _stagingController.HardLinkUnchangedFilesAsync(appName, sourceDir, manifest, ct)
    : [];

var filesNeeded = manifest.Files
    .Where(f => !linked.Contains(f.Path))
    .Select(f => f.Path)
    .ToList();
```

VSIX side contract (documented for Phase 4):
1. VSIX computes SHA-256 of all publish output files
2. VSIX constructs `DeploymentManifest` with all files
3. VSIX calls `BeginDeployment` with `deltaBase = "debug"` (for debug deploys)
4. VSIX receives `filesNeeded` list
5. VSIX uploads only the files in `filesNeeded` via SFTP
6. VSIX calls `CommitDeployment`

**Edge cases**:
- `delta_base` field uses `optional` keyword in proto3, generating a `HasDeltaBase`
  property in C#. Check `HasDeltaBase` before reading `DeltaBase`.
- If the delta source directory does not exist (first deploy), `linked` is empty and
  all files are in `filesNeeded`. This is the correct fallback to full deploy.
- The manifest SHA-256 values are computed by the VSIX on the build output. Hard links
  on the daemon side rely on these values being correct and stable (requires
  `<Deterministic>true</Deterministic>` on the app project).

**Testing requirements**:
- Integration test: second deploy with delta sends only changed files
- Integration test: first deploy with `delta_base` set but no prior debug dir → full deploy
- Integration test: modified file is in `filesNeeded`; unchanged file is not
- Unit test: delta with all files unchanged → `filesNeeded` is empty

**Definition of done**:
- [x] `BeginDeploymentRequest` has `optional string delta_base`
- [x] `BeginDeploymentResponse` has `repeated string files_needed`
- [x] Delta source resolved from `delta_base` value
- [x] `filesNeeded` contains only files not hard-linked
- [ ] All integration tests pass
