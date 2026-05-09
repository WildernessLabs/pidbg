# PiDbg VSIX — Sequence Diagrams

---

## 1. Extension Load → Ready

```mermaid
sequenceDiagram
    participant VS as Visual Studio 2026
    participant Pkg as PiDbgPackage
    participant DI as DI Container
    participant DR as DeviceRegistry
    participant OW as Output Window
    participant CMD as Commands

    VS->>Pkg: InitializeAsync()
    activate Pkg
    Pkg->>Pkg: base.InitializeAsync()
    Pkg->>DI: DiContainerBuilder.Build(this)
    DI-->>Pkg: IServiceProvider

    Pkg->>OW: EnsureOutputPaneAsync() [UI thread]
    OW-->>Pkg: IVsOutputWindowPane "PiDbg"

    Pkg->>DR: InitializeAsync() [background]
    DR->>DR: Load %LOCALAPPDATA%\PiDbg\devices.json
    DR-->>Pkg: N devices loaded

    Pkg->>CMD: RegisterCommandHandlers() [UI thread]
    CMD-->>Pkg: Commands registered in vsct menu

    Pkg->>VS: Register MEF exports active (via package load)
    deactivate Pkg
    Note over VS,DR: Ready. No SSH connections open.
```

---

## 2. F5 — Full Debug Session (first run)

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant VS as Visual Studio
    participant LP as LaunchProvider
    participant DSO as DebugSessionOrchestrator
    participant Build as VsBuildService
    participant Conn as DeviceConnectionFactory
    participant SSH as SshConnectionManager
    participant AC as AgentClient (gRPC)
    participant SFTP as SftpTransferService
    participant Dbg as VS Debugger Engine

    Dev->>VS: Press F5
    VS->>LP: QueryDebugTargetsAsync()
    LP->>LP: Read active RaspberryPiLaunchProfile
    LP->>DSO: PrepareDebugTargetAsync(profile, noDebug=false)
    activate DSO

    Note over DSO: Switch to background thread

    DSO->>Conn: GetOrCreateConnectionAsync(deviceId)
    Conn->>SSH: ConnectAsync(SshConnectionOptions)
    SSH->>SSH: TCP connect → SSH handshake → auth
    SSH-->>Conn: Connected
    Conn->>Conn: Open ForwardedPortLocal (gRPC tunnel: localA → Pi:50051)
    Conn->>AC: Create GrpcChannel(http://localhost:localA)
    Conn-->>DSO: IDeviceConnection

    DSO->>AC: PingAsync()
    AC-->>DSO: Pong (agent v1.1.0)

    DSO->>AC: GetVsdbgInfoAsync()
    AC-->>DSO: VsdbgInfo(installed=true, v17.x)

    Note over DSO: Clean-slate (§ doc-10)
    DSO->>AC: StartSession → agent kills stale processes first

    DSO->>Build: BuildAndPublishAsync(project, "Debug", "linux-arm64")
    Build->>VS: IBuildManager.BuildAsync(target=Publish)
    VS-->>Build: Build succeeded, publish dir ready
    Build-->>DSO: PublishResult(dir, fileCount, totalBytes)

    DSO->>AC: BeginDeploymentAsync(appName, fileCount, totalBytes)
    AC-->>DSO: DeploymentInfo(deploymentId, stagingPath)

    loop For each file
        DSO->>SFTP: UploadFileAsync(localFile, remoteStagingFile)
        SFTP-->>DSO: Progress
    end

    DSO->>AC: CommitDeploymentAsync(deploymentId, manifest)
    AC-->>DSO: CommitResponse(success=true)

    DSO->>SSH: AddLocalForwardAsync(remotePort=4024)
    SSH-->>DSO: ForwardedPort(localPort=B)

    DSO->>AC: StartDebugSessionAsync(StartSessionRequest)
    AC-->>DSO: StartSessionResponse(sessionId, vsdbgPort=4024, vsdbgPid=4823)

    DSO->>DSO: Build VsDebugTargetInfo4(bstrOptions="transport=tcp;host=127.0.0.1;port=B")
    DSO-->>LP: DebugLaunchSettings

    deactivate DSO
    LP-->>VS: [IDebugLaunchSettings]

    VS->>Dbg: LaunchDebugTargets4(VsDebugTargetInfo4)
    Dbg->>Dbg: Connect to localhost:B (tunneled to Pi:4024)
    Dbg-->>VS: Debugger attached
    VS-->>Dev: Breakpoints active, debugging
```

---

## 3. Ctrl+F5 — Run Without Debugging

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant VS as Visual Studio
    participant LP as LaunchProvider
    participant DSO as DebugSessionOrchestrator
    participant AC as AgentClient

    Dev->>VS: Press Ctrl+F5
    VS->>LP: QueryDebugTargetsAsync(NoDebug=true)
    LP->>DSO: PrepareDebugTargetAsync(profile, noDebug=true)

    Note over DSO: Connect, clean-slate, build, deploy (same as F5)
    Note over DSO: Skip vsdbg, skip tunnel

    DSO->>AC: StartNoDebugSessionAsync(StartSessionRequest)
    Note over AC: Agent runs: dotnet App.dll [args]
    AC-->>DSO: StartSessionResponse(appPid=4829)

    DSO->>DSO: Build VsDebugTargetInfo4(dlo=DLO_CreateProcess, no engine)
    DSO-->>LP: DebugLaunchSettings
    LP-->>VS: [IDebugLaunchSettings]

    VS-->>Dev: App running on Pi (no debugger)
    Note over Dev: Output window shows app output (optional log stream)
```

---

## 4. Stop Debug Session

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant VS as Visual Studio
    participant DbgEvt as IVsDebuggerEvents
    participant DSO as DebugSessionOrchestrator
    participant AC as AgentClient
    participant SSH as SshConnectionManager

    Dev->>VS: Press Stop (Shift+F5) or app exits
    VS->>DbgEvt: OnModeChange(DBGMODE_Design)
    DbgEvt->>DSO: OnSessionEndedAsync()
    activate DSO

    DSO->>DSO: Cancel session CancellationToken
    DSO->>AC: StopSessionAsync(sessionId, resumeMeadowDaemon=true)
    AC-->>DSO: StopSessionResponse(appExitCode=0)

    DSO->>SSH: RemoveForwardAsync(vsdbgTunnelPort)
    SSH-->>DSO: Tunnel closed

    DSO->>DSO: Log session duration to Output window
    DSO->>DSO: Update status bar: "PiDbg: Ready"
    deactivate DSO
    Note over SSH: gRPC tunnel (port A) remains open for next session
```

---

## 5. Add Device Flow

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant DM as DeviceManagerWindow
    participant DVM as AddEditDeviceViewModel
    participant Probe as SshDeviceProber
    participant Cred as CredentialService
    participant DR as DeviceRegistry

    Dev->>DM: Click "+ Add Device"
    DM->>DVM: Open AddEditDeviceDialog
    Dev->>DVM: Fill in: host, port, user, key path
    Dev->>DVM: Click "Test Connection"

    DVM->>DVM: Validate form (non-empty host, valid port)
    DVM->>Probe: ProbeDeviceAsync(host, port, user, credentials)
    activate Probe
    Probe->>Probe: SSH connect
    Probe->>Probe: Run: uname -m (verify arm64)
    Probe->>Probe: Run: cat /etc/os-release (verify Debian 12)
    Probe->>Probe: Run: dotnet --version (verify .NET 10)
    Probe->>Probe: Run: pidbg-agent --version (check if agent installed)
    Probe-->>DVM: DeviceCapabilities(arch, os, dotnet, agentInstalled)
    deactivate Probe

    DVM->>DVM: Show: "✓ Connected — arm64, Debian 12, .NET 10.0.1, agent not installed"

    Dev->>DVM: Click "Save"
    DVM->>Cred: StorePasswordAsync(tempId, password) [if password auth]
    DVM->>DR: AddDeviceAsync(AddDeviceRequest)
    DR->>DR: Assign DeviceId (Guid.NewGuid())
    DR->>DR: Write devices.json
    DR-->>DVM: DeviceRecord
    DVM->>DVM: Close dialog
    DM->>DM: Refresh device list
    Note over DM: New device shown, status "Not provisioned"
```

---

## 6. Provision Device Flow

```mermaid
sequenceDiagram
    participant Dev as Developer
    participant DM as DeviceManagerWindow
    participant SSH as SshConnectionManager
    participant SFTP as SftpTransferService
    participant OW as Output Window

    Dev->>DM: Click "Provision Device" (selected device)
    DM->>DM: Show confirmation dialog
    Dev->>DM: Confirm

    DM->>SSH: ConnectAsync(device)
    SSH-->>DM: Connected

    DM->>SFTP: UploadFileAsync(install-agent.sh, /tmp/pidbg-install.sh)
    SFTP-->>DM: Uploaded

    DM->>SSH: ExecuteCommandAsync("bash /tmp/pidbg-install.sh")
    loop stdout/stderr lines
        SSH-->>OW: Stream output → Output window
    end
    SSH-->>DM: ExitCode=0

    DM->>DM: Ping agent (30s timeout)
    DM->>DM: Update DeviceRecord.LastKnownCapabilities
    DM->>OW: "[PiDbg] Provisioning complete — agent v1.1.0 running"
    DM->>DM: Refresh device detail panel
```

---

## 7. Agent Auto-Update Flow

```mermaid
sequenceDiagram
    participant DSO as DebugSessionOrchestrator
    participant AC as AgentClient
    participant SFTP as SftpTransferService
    participant OW as Output Window

    DSO->>AC: GetStatusAsync()
    AC-->>DSO: AgentStatus(version="1.0.0")
    DSO->>DSO: Compare with bundled version "1.1.0"
    DSO->>OW: "[PiDbg] Updating agent 1.0.0 → 1.1.0..."

    DSO->>SFTP: UploadFileAsync(bundled pidbg-agent, /opt/pidbg/agent/pidbg-agent.new)
    SFTP-->>DSO: Uploaded

    DSO->>AC: PrepareUpdateAsync(newVersion="1.1.0", sha256=...)
    AC-->>DSO: PrepareUpdateResponse(ready=true)

    DSO->>AC: ApplyUpdateAsync()
    Note over AC: Agent exits → systemd restarts new version

    loop Poll Ping every 2s, up to 30s
        DSO->>AC: PingAsync()
        alt Success
            AC-->>DSO: Pong(version="1.1.0")
        else Unavailable (restarting)
            AC-->>DSO: RpcException(Unavailable)
        end
    end

    DSO->>OW: "[PiDbg] Agent updated to 1.1.0"
    Note over DSO: Continue with normal F5 sequence
```
