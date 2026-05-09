# Meadow.Daemon — Lifecycle Sequence Diagrams

---

## 1. Daemon Startup

```mermaid
sequenceDiagram
    participant systemd
    participant Host as Generic Host
    participant SS as StateStore
    participant PMS as ProcessMonitorService
    participant DM as DeploymentManager
    participant HR as HealthReporterService
    participant Kestrel

    systemd->>Host: ExecStart meadow-daemon
    activate Host

    Host->>Host: ConfigureServices() — register all singletons
    Host->>Kestrel: Bind 127.0.0.1:50051 (gRPC)
    Host->>Kestrel: Bind 127.0.0.1:5000 (REST)
    Kestrel-->>Host: Ports bound

    Host->>SS: InitializeAsync()
    SS->>SS: Load apps.json, sessions.json
    SS-->>Host: State loaded (N apps, M sessions)

    Host->>PMS: StartAsync()
    PMS->>PMS: ReconcileStateAsync()
    note over PMS: For each app in apps.json:
    PMS->>PMS: Check /proc/{savedPid}/cmdline
    alt Process alive and matches
        PMS->>PMS: Re-adopt process
    else Process dead and autoStart=true
        PMS->>PMS: Schedule restart (3s delay)
    end
    PMS->>PMS: Heal symlink/state discrepancies

    Host->>HR: StartAsync()
    HR->>HR: Begin 30s periodic health snapshots

    Host->>systemd: sd_notify(READY=1)
    deactivate Host
    note over systemd: Service is ready
```

---

## 2. Production Deployment (via gRPC from VSIX)

```mermaid
sequenceDiagram
    participant VSIX as VSIX (AgentClient)
    participant GS as MeadowDaemonGrpcService
    participant DM as DeploymentManager
    participant VS as VersionStore
    participant SC as StagingController
    participant MV as ManifestVerifier
    participant SS as StateStore

    VSIX->>GS: BeginDeployment(appName, slot=Production, files)
    GS->>DM: BeginDeploymentAsync(appName, Production, files)
    DM->>DM: Acquire _appLocks[appName]
    DM->>SC: BeginStagingAsync(appName)
    SC->>SC: Delete any leftover staging/
    SC->>SC: Create staging/
    DM->>VS: AllocateNextVersionLabelAsync(appName)
    VS-->>DM: label = "000004"
    DM-->>GS: BeginDeploymentResult(deployId, stagingPath, "000004")
    GS-->>VSIX: BeginDeploymentResponse

    Note over VSIX: Upload files via SFTP (parallel)

    VSIX->>GS: CommitDeployment(deployId, manifest)
    GS->>DM: CommitDeploymentAsync(appName, Production, manifest)
    DM->>MV: VerifyAsync(stagingPath, manifest)
    MV->>MV: SHA-256 all files (parallel)
    MV-->>DM: Verified OK
    DM->>DM: Directory.Move(staging → versions/000004)
    DM->>DM: Write manifest.json
    DM->>DM: UpdateSymlink(active → versions/000004)
    DM->>DM: PruneDeploymentsAsync(keep=3)
    DM->>SS: WriteAppsAsync()
    DM->>DM: Release _appLocks[appName]
    DM-->>GS: CommitResult(success=true)
    GS-->>VSIX: CommitDeploymentResponse
```

---

## 3. Debug Deployment + Session Start

```mermaid
sequenceDiagram
    participant VSIX as VSIX (AgentClient)
    participant GS as MeadowDaemonGrpcService
    participant DM as DeploymentManager
    participant PM as ProcessManager
    participant DSM as DebugSessionManager
    participant VL as VsdbgLauncher

    Note over VSIX,PM: Step 1 — Stop any running app (clean-slate)
    VSIX->>GS: StopApplication(appName)
    GS->>PM: StopApplicationAsync(appName)
    PM->>PM: SIGTERM → wait → SIGKILL if needed
    PM-->>GS: StopResult
    GS-->>VSIX: StopApplicationResponse

    Note over VSIX,DM: Step 2 — Deploy debug build
    VSIX->>GS: BeginDeployment(appName, slot=Debug, files)
    GS->>DM: BeginDeploymentAsync(appName, Debug, files)
    DM->>DM: Acquire _appLocks[appName]
    DM-->>GS: BeginDeploymentResult(stagingPath, "debug")
    GS-->>VSIX: BeginDeploymentResponse

    Note over VSIX: Upload files via SFTP

    VSIX->>GS: CommitDeployment(deployId, manifest)
    GS->>DM: CommitDeploymentAsync(appName, Debug, manifest)
    DM->>DM: Verify + delete old debug/ + Directory.Move(staging→debug/)
    DM-->>GS: CommitResult
    GS-->>VSIX: CommitDeploymentResponse

    Note over VSIX,VL: Step 3 — Start debug session
    VSIX->>GS: StartDebugSession(appName, mode=Launch, vsdbgVersion)
    GS->>DSM: StartDebugSessionAsync(request)
    DSM->>DSM: EnsureVsdbgAsync(vsdbgVersion)
    DSM->>VL: LaunchAsync(port=4024, attachPid=null)
    VL->>VL: spawn vsdbg --server --port 4024 --interpreter=vscode
    VL->>VL: Poll /proc/net/tcp6 until port 4024 LISTEN
    VL-->>DSM: VsdbgHandle(pid=4823, port=4024)
    DSM->>DSM: Record session
    DSM-->>GS: StartSessionResult(sessionId, vsdbgPid=4823, vsdbgPort=4024)
    GS-->>VSIX: StartDebugSessionResponse

    Note over VSIX: SSH tunnel: localhost:B → Pi:4024
    Note over VSIX: VS debugger attaches to localhost:B
```

---

## 4. Stop Debug Session

```mermaid
sequenceDiagram
    participant VSIX as VSIX (AgentClient)
    participant GS as MeadowDaemonGrpcService
    participant DSM as DebugSessionManager
    participant PM as ProcessManager
    participant SS as StateStore

    VSIX->>GS: StopDebugSession(sessionId, resumeMeadowDaemon=true)
    GS->>DSM: StopDebugSessionAsync(sessionId, resumeMeadowDaemon=true)

    DSM->>DSM: Lookup session record
    DSM->>DSM: SIGTERM vsdbg (pid=4823)
    DSM->>DSM: Wait up to 3s
    DSM->>DSM: SIGKILL if still alive

    DSM->>DSM: Remove from _sessions
    DSM->>SS: WriteSessionsAsync([])

    alt resumeMeadowDaemon=true
        DSM->>PM: StartApplicationAsync(appName, useDebugSlot=false)
        Note over PM: Starts production slot (active symlink target)
        PM-->>DSM: Running (pid=4830)
    end

    DSM-->>GS: StopSessionResult(ok=true)
    GS-->>VSIX: StopDebugSessionResponse
```

---

## 5. Daemon Self-Update

```mermaid
sequenceDiagram
    participant VSIX as VSIX (AgentClient)
    participant GS as MeadowDaemonGrpcService
    participant SFTP as SFTP (SSH.NET)
    participant HA as IHostApplicationLifetime
    participant systemd

    VSIX->>GS: GetDaemonVersion()
    GS-->>VSIX: DaemonVersion(version="1.0.0", protocolVersion=1)
    Note over VSIX: Compare with bundled version "1.1.0"

    VSIX->>GS: PrepareUpdate(newVersion="1.1.0", sha256=..., sizeBytes=...)
    GS->>GS: Validate newVersion != currentVersion
    GS->>GS: Path = /opt/meadow/daemon/meadow-daemon.new
    GS-->>VSIX: PrepareUpdateResponse(ready=true, uploadPath=...)

    VSIX->>SFTP: Upload meadow-daemon to /opt/meadow/daemon/meadow-daemon.new
    SFTP-->>VSIX: Upload complete

    VSIX->>GS: ApplyUpdate()
    GS->>GS: Verify SHA-256(meadow-daemon.new) == expected
    GS->>GS: chmod +x meadow-daemon.new
    GS->>GS: rename(meadow-daemon.new → meadow-daemon)
    GS->>HA: StopApplication()
    GS-->>VSIX: ApplyUpdateResponse(success=true)
    Note over GS: Daemon exits cleanly

    systemd->>systemd: Restart=on-failure triggers restart
    systemd->>systemd: ExecStart: new meadow-daemon binary

    loop Poll every 2s, timeout 30s
        VSIX->>GS: PingAsync()
        alt Success
            GS-->>VSIX: Pong(version="1.1.0")
        else Unavailable (restarting)
            GS-->>VSIX: RpcException(Unavailable)
        end
    end
    Note over VSIX: Self-update complete
```

---

## 6. OTA Update (Cloud Path)

```mermaid
sequenceDiagram
    participant Cloud as Meadow Cloud (MQTT)
    participant OTA as OtaUpdateService
    participant CA as CloudAuthClient
    participant UA as UpdateApplicator
    participant PM as ProcessManager
    participant DM as DeploymentManager

    Cloud->>OTA: MQTT publish on {OID}/ota/{ID}
    OTA->>OTA: Parse OTA message (version, download URL, checksum)

    OTA->>CA: GetJwtAsync()
    CA->>CA: Sign auth challenge with SSH private key
    CA-->>OTA: JWT token

    OTA->>UA: ApplyUpdateAsync(version, downloadUrl, jwt)
    UA->>UA: HTTP GET downloadUrl (with JWT auth)
    UA->>UA: Verify SHA-256 of downloaded package
    UA->>UA: Extract MPAK to staging

    UA->>PM: StopApplicationAsync(appName)
    PM-->>UA: Stopped

    UA->>DM: CommitDeploymentAsync(appName, Production, manifest)
    DM->>DM: versions/000005/ + symlink swap
    DM-->>UA: Committed

    UA->>PM: StartApplicationAsync(appName, useDebugSlot=false)
    PM-->>UA: Running (pid=4831)

    OTA->>Cloud: Publish acknowledgement
```

---

## 7. Process Auto-Restart (Crash Recovery)

```mermaid
sequenceDiagram
    participant App as Managed App Process
    participant PMS as ProcessMonitorService
    participant PM as ProcessManager
    participant SS as StateStore

    App->>App: Crash (SIGSEGV / unhandled exception)
    App-->>PM: ProcessExited event (exitCode != 0)

    PM->>PM: Update ManagedAppState: State=Failed, Pid=null
    PM->>SS: UpdatePidAsync(appName, pid=null)

    PMS->>PMS: CheckTrackedProcessesAsync (next 5s tick)
    PMS->>PMS: IsProcessAlive(savedPid) → false
    PMS->>PMS: ShouldAutoRestart("MyApp") → true (< 5 restarts in 60s)

    PMS->>PMS: Wait 3s (restart delay)
    PMS->>PM: StartApplicationAsync("MyApp", useDebugSlot=false)
    PM->>PM: Spawn dotnet MyApp.dll
    PM->>SS: UpdatePidAsync("MyApp", newPid=4832)
    PM-->>PMS: Running (pid=4832)
```
