# PiDbg — Lifecycle Diagrams

All diagrams use Mermaid syntax (render in GitHub, VS Code with Mermaid extension, etc.)

---

## 1. Agent Startup Sequence

```mermaid
sequenceDiagram
    participant systemd
    participant Agent as PiDbg.Agent
    participant HS as AgentHealthService
    participant VM as VsdbgManager
    participant DM as DeploymentManager
    participant Kestrel

    systemd->>Agent: Start (pidbg-agent.service)
    Agent->>Agent: Build DI container
    Agent->>HS: StartAsync()
    HS->>HS: Collect OS info, disk info
    Agent->>VM: StartAsync()
    VM->>VM: Check /opt/pidbg/vsdbg/vsdbg exists
    VM->>VM: Read .version file
    Agent->>DM: StartAsync()
    DM->>DM: Clean up orphaned staging dirs
    DM->>DM: Load deployment records
    Agent->>Kestrel: Start gRPC server on 127.0.0.1:50051
    Kestrel->>Agent: Bound and listening
    Agent->>Agent: Log "PiDbg.Agent ready"
    Note over Agent: Ready to accept connections
```

---

## 2. VS Extension Load Sequence

```mermaid
sequenceDiagram
    participant VS as Visual Studio 2026
    participant Pkg as PiDbgPackage (AsyncPackage)
    participant DR as DeviceRegistry
    participant DCF as DeviceConnectionFactory
    participant OW as OutputWindowService

    VS->>Pkg: InitializeAsync()
    Pkg->>Pkg: Register DI container
    Pkg->>DR: InitializeAsync() — load devices.json
    Pkg->>OW: EnsureOutputPaneAsync()
    Pkg->>VS: Register commands (Add Device, Manage Devices)
    Pkg->>VS: Register debug profile provider (RaspberryPiDebugProfileProvider)
    Pkg->>VS: Register debug launch provider (RaspberryPiDebugLaunchProvider)
    Pkg->>VS: Register property page
    VS->>Pkg: Package initialized
    Note over Pkg: Extension active, no SSH connection yet
    Note over Pkg: Connection deferred until first F5 or device probe
```

---

## 3. Full Debug Session Lifecycle

```mermaid
stateDiagram-v2
    [*] --> Idle : Extension loaded

    Idle --> Connecting : F5 pressed
    Connecting --> Connected : SSH session established
    Connecting --> Failed : Connection timeout / auth failure

    Connected --> Checking : Ping agent
    Checking --> Building : Agent alive
    Checking --> Failed : Agent not running

    Building --> Deploying : dotnet publish success
    Building --> Failed : Build error

    Deploying --> Deploying : Uploading files...
    Deploying --> LaunchingVsdbg : Commit accepted
    Deploying --> Failed : SHA-256 mismatch / disk full

    LaunchingVsdbg --> WaitingForVsdbg : vsdbg process spawned
    WaitingForVsdbg --> AttachingDebugger : vsdbg port bound
    WaitingForVsdbg --> Failed : vsdbg start timeout

    AttachingDebugger --> Debugging : VS debugger attached
    AttachingDebugger --> Failed : Attach timeout

    Debugging --> Debugging : Breakpoints / stepping...
    Debugging --> Stopping : User presses Stop / process exits

    Stopping --> Idle : Session cleaned up
    Failed --> Idle : Error displayed, resources cleaned up
```

---

## 4. Deployment Lifecycle

```mermaid
sequenceDiagram
    participant VSIX
    participant Deploy as DeploymentService
    participant SFTP as SftpTransferService
    participant AC as AgentClient
    participant Agent as PiDbg.Agent
    participant DM as DeploymentManager

    VSIX->>Deploy: DeployAsync(publishDir, connection, options)
    Deploy->>Deploy: PackageAsync() — scan files, compute SHA-256
    Deploy->>AC: BeginDeploymentAsync(appId, fileCount, totalBytes)
    AC->>Agent: gRPC BeginDeployment
    Agent->>DM: BeginDeploymentAsync()
    DM->>DM: mkdir /opt/pidbg/apps/<id>/staging/
    Agent-->>AC: DeploymentInfo(deploymentId, stagingPath)
    
    loop For each file in manifest
        Deploy->>SFTP: UploadFileAsync(localFile, remoteStagingPath)
        SFTP->>Agent: SFTP write (64KB chunks)
        SFTP-->>Deploy: Progress update
        Deploy-->>VSIX: IProgress<DeploymentProgress> update
    end

    Deploy->>AC: CommitDeploymentAsync(deploymentId, manifest)
    AC->>Agent: gRPC CommitDeployment
    Agent->>DM: CommitDeploymentAsync()
    DM->>DM: Verify all SHA-256 hashes
    alt All hashes match
        DM->>DM: rename current → previous
        DM->>DM: rename staging → current
        DM->>DM: Write deployment.json
        Agent-->>AC: CommitResult(success=true)
        AC-->>Deploy: CommitDeploymentResponse
        Deploy-->>VSIX: DeploymentResult(success=true, deploymentId)
    else Hash mismatch
        DM->>DM: rm -rf staging/
        Agent-->>AC: CommitResult(success=false, error)
        Deploy-->>VSIX: DeploymentResult(success=false, error)
    end
```

---

## 5. vsdbg Attach Flow

```mermaid
sequenceDiagram
    participant VSIX
    participant DSO as DebugSessionOrchestrator
    participant PFM as PortForwardingManager
    participant AC as AgentClient
    participant Agent as PiDbg.Agent
    participant VM as VsdbgManager
    participant VL as VsdbgLauncher
    participant MDC as MeadowDaemonClient
    participant vsdbg
    participant VSD as VS Debugger Engine

    VSIX->>DSO: StartSessionAsync(profile, connection, options)
    DSO->>AC: GetVsdbgInfoAsync()
    AC->>Agent: gRPC GetVsdbgInfo
    Agent->>VM: GetStatusAsync()
    Agent-->>AC: VsdbgInfo(installed=true, version="17.x")
    
    DSO->>AC: StartDebugSessionAsync(StartSessionRequest)
    AC->>Agent: gRPC StartSession
    Agent->>MDC: RequestProcessStopAsync()
    MDC->>MDC: PUT http://127.0.0.1:5000/api/... (stop managed process)
    Agent->>VM: LaunchAsync(VsdbgLaunchOptions)
    VM->>VL: LaunchAsync()
    VL->>vsdbg: spawn: vsdbg --server --port 4024 -- dotnet App.dll
    vsdbg->>vsdbg: Bind TCP 127.0.0.1:4024
    vsdbg->>VL: Port bound (detected via socket probe)
    VL-->>VM: VsdbgProcess(pid, port)
    Agent-->>AC: StartSessionResponse(sessionId, vsdbgPort=4024, vsdbgPid)
    
    DSO->>PFM: OpenPortForwardAsync(remotePort=4024)
    PFM->>PFM: Allocate local ephemeral port N
    PFM->>PFM: Add ForwardedPortLocal(N → Pi:4024)
    PFM-->>DSO: IForwardedPort(localPort=N)
    
    DSO->>DSO: Build VsDebugTargetInfo4
    Note over DSO: Engine: {2E36F1D4-B23C-435D-AB41-18E608940038}<br/>Transport: TCP<br/>Address: localhost:N
    DSO->>VSD: IVsDebugger4.LaunchDebugTargets4(targets)
    VSD->>VSD: Connect to localhost:N (SSH-tunneled to Pi:4024)
    VSD-->>vsdbg: ICorDebug protocol handshake
    vsdbg->>vsdbg: Launch .NET 10 app under debugger
    VSD-->>VSIX: Debugger attached
    Note over VSD,vsdbg: Debugging session active
```

---

## 6. Connection State Machine

```mermaid
stateDiagram-v2
    [*] --> Disconnected : Initial state

    Disconnected --> Connecting : User triggers F5 or Device Manager
    Connecting --> Connected : SSH handshake + auth success
    Connecting --> Disconnected : Timeout / auth failure (error shown)

    Connected --> Reconnecting : SSH connection dropped
    Reconnecting --> Connected : Reconnect success
    Reconnecting --> Disconnected : Max retries exceeded

    Connected --> Disconnected : User closes VS / Device Manager disconnect
```

---

## 7. Agent Self-Update Lifecycle

```mermaid
sequenceDiagram
    participant VSIX
    participant AC as AgentClient
    participant Agent as PiDbg.Agent
    participant GH as GitHub Releases API

    VSIX->>AC: GetStatusAsync()
    AC->>Agent: gRPC GetStatus
    Agent-->>AC: AgentStatus(version="1.0.0")
    VSIX->>VSIX: Compare with bundled expected version "1.1.0"
    VSIX->>VSIX: Show "Agent update available" in Output window
    VSIX->>VSIX: User confirms update (or auto-update if configured)
    
    VSIX->>VSIX: Download new agent binary from GitHub Release
    VSIX->>SFTP: Upload to /opt/pidbg/agent/pidbg-agent.new
    VSIX->>AC: UpdateAgentAsync(new version info)
    AC->>Agent: gRPC UpdateAgent(triggerRestart=true)
    Agent->>Agent: systemctl --user restart pidbg-agent.service
    Note over Agent: Process exits and systemd restarts it
    Agent->>Agent: [new version starts]
    VSIX->>VSIX: Poll Ping with 30s timeout until new version responds
    VSIX->>AC: PingAsync()
    AC-->>VSIX: Pong(version="1.1.0")
    VSIX->>VSIX: Update confirmed
```
