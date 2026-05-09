# PiDbg — Deployment Flow

---

## 1. Overview

Deployment is the process of transferring a `dotnet publish` output to the Raspberry Pi
and making it the active version. It is triggered automatically on every F5 press (full
deploy) and can also be triggered manually via the Device Manager.

The design priorities are:
1. **Atomicity** — a failed deploy never corrupts the running app
2. **Speed** — minimize unnecessary file transfers
3. **Integrity** — SHA-256 verification before activation

There is no "previous version" retention. This is a debugging tool, not a production
deployment system. If a deploy goes wrong, the fix is to fix the code and press F5 again —
the developer always has the source. Keeping a previous copy wastes disk space on a
resource-constrained SD card with no practical benefit over a rebuild.

---

## 2. Build → Publish Phase

The VSIX triggers MSBuild via `IBuildManager` (VS SDK).

Build properties injected:
```xml
<PropertyGroup>
  <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>
  <Configuration>Debug</Configuration>          <!-- Always Debug for remote debug -->
  <PublishSingleFile>false</PublishSingleFile>   <!-- Keep as separate files for delta -->
  <SelfContained>false</SelfContained>           <!-- Framework-dependent by default -->
  <PublishReadyToRun>false</PublishReadyToRun>   <!-- Crossgen would mismatch arch -->
  <DebugType>portable</DebugType>                <!-- .pdb files required for debugging -->
  <DebugSymbols>true</DebugSymbols>
</PropertyGroup>
```

Important: `<SelfContained>false</SelfContained>` means .NET 10 runtime must be installed
on the Pi. The provisioning script handles this. Self-contained publish is available as
an opt-in profile setting for air-gapped Pis (increases deploy size by ~60 MB).

Publish output directory: `<project>/bin/Debug/net10.0/linux-arm64/publish/`

The VSIX waits for the build to complete (success or failure) before proceeding.
Build errors surface in the VS Error List as normal.

---

## 3. Package Phase

`IDeploymentPackager.PackageAsync()` reads the publish output directory.

```
Input:  /local/publish/
        ├── MyApp.dll
        ├── MyApp.pdb
        ├── MyApp.runtimeconfig.json
        ├── MyApp.deps.json
        ├── appsettings.json
        └── (other dependencies)

Output: DeploymentPackage
        ├── Id: Guid.NewGuid()
        ├── AppName: "MyApp"
        ├── Manifest:
        │   ├── Entry { RelativePath: "MyApp.dll", Sha256: "...", Size: 102400 }
        │   ├── Entry { RelativePath: "MyApp.pdb", Sha256: "...", Size: 204800 }
        │   └── ...
        └── TotalSize: 1048576
```

SHA-256 computation is parallelized across files using `Parallel.ForEachAsync()` with
concurrency limit = `Environment.ProcessorCount`.

Files explicitly excluded from deployment:
- `*.vshost.*` (VS hosting process artifacts)
- `*.web.config` (IIS artifacts)
- `.DS_Store`, `Thumbs.db`
- `**/obj/**` (should not appear in publish output, but guard against it)

---

## 4. Transfer Phase

### 4.1 Begin Deployment
VSIX calls `AgentClient.BeginDeploymentAsync()` with app ID and expected file count.
Agent creates: `/opt/pidbg/apps/<appName>/staging-<deploymentId>/`

App ID is derived from the project name (e.g., "MyApp"). This ensures different projects
deploy to different directories, and multiple developers targeting the same Pi coexist
(each project has its own slot).

### 4.2 File Upload
Files are uploaded via SFTP to the staging directory.

Upload order: 
1. Non-DLL files first (config, runtime config) — small, fast
2. PDB files (needed for debugging, mid-size)
3. DLL files (largest, including runtime deps)
4. Main app DLL last (last file written = deployment can be considered "receiving" until then)

Progress reporting granularity: per-file progress + bytes-within-file for large files.

```
Output window:
[PiDbg] Deploying MyApp → raspberrypi (192.168.1.100)
[PiDbg] Uploading MyApp.runtimeconfig.json (1/15)
[PiDbg] Uploading MyApp.deps.json (2/15)
[PiDbg] Uploading MyApp.pdb (3/15) [204KB]
[PiDbg] Uploading MyApp.dll (4/15) [1.2MB]
...
[PiDbg] Upload complete: 4.1 MB in 2.3s
```

### 4.3 Delta Upload (Phase 2)
Before uploading, VSIX calls `AgentClient.GetCurrentDeploymentManifestAsync()`.
The agent returns the SHA-256 manifest of the currently deployed version.
VSIX compares manifests and only uploads files with different hashes.

On a typical iterative development cycle (change one source file), this reduces upload
from the full publish output to 2–3 files (changed DLL + PDB).

---

## 5. Commit Phase

### 5.1 VSIX sends manifest
```csharp
await agentClient.CommitDeploymentAsync(new CommitDeploymentRequest
{
    DeploymentId = deploymentId,
    Manifest = manifest  // all expected files + SHA-256 hashes
});
```

### 5.2 Agent validates
For each file in the manifest:
1. Check file exists in staging directory
2. Compute SHA-256 of the file
3. Compare with manifest hash
4. If any mismatch: abort

```
Agent log:
[INFO] Verifying deployment abc123: 15/15 files
[INFO] MyApp.dll SHA256: OK
[INFO] MyApp.pdb SHA256: OK
...
[INFO] All files verified
```

### 5.3 Atomic swap
```
Before:
  /opt/pidbg/apps/MyApp/current/         ← active version (app may be running from here)
  /opt/pidbg/apps/MyApp/staging-<id>/   ← verified new version

Swap sequence:
  rm -rf current/                        (if exists — old version discarded)
  rename staging-<id>/ → current/

After:
  /opt/pidbg/apps/MyApp/current/         ← new version, ready to run
```

The rename is atomic on ext4 (`rename(2)` syscall). If the agent crashes between the
`rm -rf` and the rename, the staging directory remains and is recovered on restart.
The brief gap where `current/` is absent between delete and rename is acceptable — vsdbg
has not been started yet at this point in the sequence.

---

## 6. Deployment Failure Handling

| Failure Point | Behavior |
|---------------|----------|
| Build fails | MSBuild reports error to VS Error List; deploy never starts |
| SSH connection lost during SFTP | Upload aborted; staging dir left (cleaned on next F5) |
| Agent rejects BeginDeployment (disk full) | Error returned to VSIX; shown in Output window |
| SHA-256 mismatch on commit | Agent deletes staging; VSIX shows error; fix code and press F5 |
| rename() fails (permissions) | Agent returns error; staging preserved for diagnosis |
| Agent crashes between rm -rf and rename | On restart, agent scans for orphaned staging dirs and cleans them |

---

## 7. Deployment State on Agent Restart

On startup, `DeploymentManager` scans `/opt/pidbg/apps/*/` and:
- Deletes any `staging-*` directories (incomplete upload — clean start)
- Logs a warning for any app with no `current/` directory

This ensures the agent is always in a clean state after restart, regardless of how it stopped.

---

## 8. Deployment Timing Targets

| Operation | Target Time | Notes |
|-----------|-------------|-------|
| Build + Publish | 2–10s | Depends on project size; cached incremental |
| Manifest computation | < 0.5s | Parallel SHA-256 on dev machine |
| Full deploy (10 MB framework-dep) | 15–30s | Depends on network speed to Pi |
| Delta deploy (changed DLL only) | 2–5s | Single file, typical iterative cycle |
| Commit + verify | 1–3s | Pi CPU for SHA-256 computation |
| Total F5 → vsdbg ready (first deploy) | 25–45s | |
| Total F5 → vsdbg ready (delta) | 10–15s | |

These targets assume a gigabit LAN. Wi-Fi adds 5–20% overhead.
