# PiDbg — Major Interfaces

All interfaces use async/await patterns. All async methods accept a `CancellationToken`.
All interfaces are designed for dependency injection. No static state.

---

## VSIX-side Interfaces

### IDeviceRegistry
**Project:** PiDbg.DeviceManagement  
**Purpose:** Persistent storage of device records. Thread-safe, async.

```csharp
public interface IDeviceRegistry
{
    Task<IReadOnlyList<DeviceRecord>> GetAllDevicesAsync(CancellationToken ct = default);
    Task<DeviceRecord?> GetDeviceAsync(Guid deviceId, CancellationToken ct = default);
    Task<DeviceRecord> AddDeviceAsync(AddDeviceRequest request, CancellationToken ct = default);
    Task<DeviceRecord> UpdateDeviceAsync(Guid deviceId, UpdateDeviceRequest request, CancellationToken ct = default);
    Task RemoveDeviceAsync(Guid deviceId, CancellationToken ct = default);
    event EventHandler<DeviceRegistryChangedEventArgs> RegistryChanged;
}
```

---

### IDeviceDiscoveryService
**Project:** PiDbg.DeviceManagement  
**Purpose:** Discover Pi devices on the local network. Phase 1 supports manual add only.
Phase 2 adds mDNS (the agent advertises `_pidbg._tcp.local`).

```csharp
public interface IDeviceDiscoveryService
{
    IAsyncEnumerable<DiscoveredDevice> DiscoverAsync(CancellationToken ct);
    Task<DeviceCapabilities> ProbeDeviceAsync(string host, int port, string username,
        SshCredentials credentials, CancellationToken ct);
}
```

---

### IDeviceConnectionFactory
**Project:** PiDbg.DeviceManagement  
**Purpose:** Creates a `DeviceConnection` (SSH session + gRPC channel) for a registered device.
Connections are cached per device for the session lifetime.

```csharp
public interface IDeviceConnectionFactory
{
    Task<IDeviceConnection> GetOrCreateConnectionAsync(Guid deviceId, CancellationToken ct);
    Task CloseConnectionAsync(Guid deviceId, CancellationToken ct);
    Task CloseAllConnectionsAsync(CancellationToken ct);
}
```

---

### IDeviceConnection
**Project:** PiDbg.DeviceManagement  
**Purpose:** Represents an active connection to a device. Wraps the SSH session and the
gRPC channel, both tunneled through the same SSH connection.

```csharp
public interface IDeviceConnection : IAsyncDisposable
{
    Guid DeviceId { get; }
    DeviceRecord Device { get; }
    bool IsConnected { get; }
    IAgentClient AgentClient { get; }
    ISftpTransferService SftpTransfer { get; }
    Task<IForwardedPort> OpenPortForwardAsync(int remotePort, CancellationToken ct);
    event EventHandler<ConnectionStateChangedEventArgs> StateChanged;
}
```

---

### ISshConnectionManager
**Project:** PiDbg.Transport  
**Purpose:** Manages the lifecycle of a single SSH connection to one device.
One instance per device. Handles keepalive, reconnection, and tunnel multiplexing.

```csharp
public interface ISshConnectionManager : IAsyncDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(SshConnectionOptions options, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<IForwardedPort> AddLocalForwardAsync(int remotePort, CancellationToken ct);
    Task RemoveForwardAsync(IForwardedPort port, CancellationToken ct);
    Task ExecuteCommandAsync(string command, CancellationToken ct);
    Task<string> ExecuteCommandWithOutputAsync(string command, CancellationToken ct);
    event EventHandler<SshConnectionStateEventArgs> ConnectionStateChanged;
}
```

---

### ISftpTransferService
**Project:** PiDbg.Transport  
**Purpose:** File transfer to/from the Pi via SFTP. Exposes progress reporting.

```csharp
public interface ISftpTransferService
{
    Task UploadFileAsync(string localPath, string remotePath,
        IProgress<TransferProgress>? progress, CancellationToken ct);
    Task UploadDirectoryAsync(string localPath, string remotePath,
        IProgress<TransferProgress>? progress, CancellationToken ct);
    Task<string> DownloadTextAsync(string remotePath, CancellationToken ct);
    Task UploadTextAsync(string remotePath, string content, CancellationToken ct);
    Task DeleteRemoteFileAsync(string remotePath, CancellationToken ct);
    Task DeleteRemoteDirectoryAsync(string remotePath, CancellationToken ct);
    Task<bool> RemotePathExistsAsync(string remotePath, CancellationToken ct);
    Task<IReadOnlyList<RemoteFileInfo>> ListRemoteDirectoryAsync(string remotePath, CancellationToken ct);
}
```

---

### IAgentClient
**Project:** PiDbg.Contracts (interface) / PiDbg.Vsix (implementation wrapping generated stub)  
**Purpose:** Typed wrapper over the gRPC generated client. Provides task-returning methods
with meaningful exceptions instead of raw `RpcException`. This is the primary interface
through which the VSIX communicates with the agent.

```csharp
public interface IAgentClient
{
    // Connectivity
    Task<AgentStatus> GetStatusAsync(CancellationToken ct);
    Task<PingResponse> PingAsync(CancellationToken ct);

    // vsdbg management
    Task<VsdbgInfo> GetVsdbgInfoAsync(CancellationToken ct);
    Task<InstallVsdbgResponse> InstallVsdbgAsync(string version, CancellationToken ct);

    // Deployment
    Task<DeploymentInfo> BeginDeploymentAsync(BeginDeploymentRequest request, CancellationToken ct);
    Task UploadDeploymentChunksAsync(Guid deploymentId,
        IAsyncEnumerable<DeploymentChunk> chunks, CancellationToken ct);
    Task<CommitDeploymentResponse> CommitDeploymentAsync(Guid deploymentId,
        DeploymentManifest manifest, CancellationToken ct);
    Task AbortDeploymentAsync(Guid deploymentId, CancellationToken ct);

    // Debug session management
    Task<StartSessionResponse> StartDebugSessionAsync(StartSessionRequest request, CancellationToken ct);
    Task<StopSessionResponse> StopDebugSessionAsync(Guid sessionId, CancellationToken ct);
    Task<SessionStatus> GetSessionStatusAsync(Guid sessionId, CancellationToken ct);

    // Log streaming
    IAsyncEnumerable<LogEvent> StreamLogsAsync(StreamLogsRequest request, CancellationToken ct);
}
```

---

### IDeploymentPackager
**Project:** PiDbg.Deployment  
**Purpose:** Reads a dotnet publish output directory and produces a `DeploymentPackage`
containing the file list, SHA-256 hashes, and metadata.

```csharp
public interface IDeploymentPackager
{
    Task<DeploymentPackage> PackageAsync(string publishOutputDirectory,
        PackageOptions options, CancellationToken ct);
}
```

---

### IDeploymentService
**Project:** PiDbg.Deployment  
**Purpose:** Orchestrates the full deploy sequence: package → transfer → commit.
Used by the VSIX debug launch provider.

```csharp
public interface IDeploymentService
{
    Task<DeploymentResult> DeployAsync(
        string publishOutputDirectory,
        IDeviceConnection connection,
        DeploymentOptions options,
        IProgress<DeploymentProgress>? progress,
        CancellationToken ct);
}
```

---

### IDebugSessionOrchestrator
**Project:** PiDbg.Vsix  
**Purpose:** Drives the entire sequence from "user pressed F5" to "debugger attached."
Called by the VS launch provider. Returns a `DebugSessionHandle` that VS can poll.

```csharp
public interface IDebugSessionOrchestrator
{
    Task<DebugSessionHandle> StartSessionAsync(
        RaspberryPiLaunchProfile profile,
        IDeviceConnection connection,
        DebugLaunchOptions launchOptions,
        CancellationToken ct);

    Task StopSessionAsync(DebugSessionHandle handle, CancellationToken ct);

    Task AttachToProcessAsync(
        RaspberryPiLaunchProfile profile,
        IDeviceConnection connection,
        int remotePid,
        CancellationToken ct);
}
```

---

## Agent-side Interfaces

### IVsdbgManager
**Project:** PiDbg.Agent  
**Purpose:** Knows where vsdbg lives on disk, validates its integrity, initiates installation.

```csharp
public interface IVsdbgManager
{
    Task<VsdbgInstallStatus> GetStatusAsync(CancellationToken ct);
    Task<bool> IsInstalledAsync(CancellationToken ct);
    Task<string> GetInstalledVersionAsync(CancellationToken ct);
    Task InstallAsync(string version, IProgress<InstallProgress>? progress, CancellationToken ct);
    string GetVsdbgExecutablePath();
}
```

---

### IVsdbgLauncher
**Project:** PiDbg.Agent  
**Purpose:** Spawns a vsdbg process with the correct arguments for a debug session.
Returns a `VsdbgProcess` handle that tracks the PID and lifecycle.

```csharp
public interface IVsdbgLauncher
{
    Task<VsdbgProcess> LaunchAsync(VsdbgLaunchOptions options, CancellationToken ct);
}

public sealed record VsdbgLaunchOptions
{
    public required int Port { get; init; }          // TCP listen port
    public required string AppPath { get; init; }   // Path to managed .dll
    public IReadOnlyList<string> AppArgs { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = ImmutableDictionary<string, string>.Empty;
    public int? AttachPid { get; init; }             // null = launch mode, non-null = attach mode
}
```

---

### IProcessLifecycleService
**Project:** PiDbg.Agent  
**Purpose:** Start, stop, and query processes. Abstraction over System.Diagnostics.Process
to enable unit testing without spawning real processes.

```csharp
public interface IProcessLifecycleService
{
    Task<ProcessHandle> StartProcessAsync(ProcessStartOptions options, CancellationToken ct);
    // SIGTERM → gracePeriod → SIGKILL. Returns true if process was found and killed.
    Task<bool> StopProcessAsync(int pid, TimeSpan gracePeriod, CancellationToken ct);
    Task<bool> IsProcessRunningAsync(int pid, CancellationToken ct);
    Task<IReadOnlyList<ProcessInfo>> ListProcessesAsync(CancellationToken ct);

    // Stale-process cleanup — used by clean-slate policy on every F5.
    // Searches /proc/*/cmdline for a dotnet process whose command line contains
    // the given app deployment path. Returns null if no match found.
    Task<ProcessInfo?> FindAppProcessAsync(string appName, CancellationToken ct);

    // Searches for a vsdbg process listening on any port in the given range
    // by reading /proc directly (no shell invocation). Returns null if none found.
    Task<ProcessInfo?> FindVsdbgProcessAsync(int portRangeStart, int portRangeEnd, CancellationToken ct);

    event EventHandler<ProcessExitedEventArgs> ProcessExited;
}
```

---

### IDeploymentManager
**Project:** PiDbg.Agent  
**Purpose:** Receives deployment uploads from VSIX, validates manifests, performs atomic swap.

```csharp
public interface IDeploymentManager
{
    Task<DeploymentStagingInfo> BeginDeploymentAsync(BeginDeploymentRequest request, CancellationToken ct);
    Task WriteChunkAsync(Guid deploymentId, DeploymentChunk chunk, CancellationToken ct);
    Task<CommitResult> CommitDeploymentAsync(Guid deploymentId,
        DeploymentManifest manifest, CancellationToken ct);
    Task AbortDeploymentAsync(Guid deploymentId, CancellationToken ct);
    Task<IReadOnlyList<DeploymentRecord>> ListDeploymentsAsync(CancellationToken ct);
}
```

---

### IMeadowDaemonClient
**Project:** PiDbg.Agent  
**Purpose:** HTTP client for the Meadow.Daemon REST API on localhost:5000.
Used to coordinate graceful handoff of managed processes before debug sessions.

```csharp
public interface IMeadowDaemonClient
{
    Task<bool> IsDaemonRunningAsync(CancellationToken ct);
    Task<MeadowProcessInfo?> GetManagedProcessInfoAsync(CancellationToken ct);
    Task<bool> RequestProcessStopAsync(CancellationToken ct);
    Task<bool> RequestProcessResumeAsync(CancellationToken ct);
}
```

---

## VS Debug Integration Interfaces (Implemented)

These are VS SDK interfaces our code implements, listed here for completeness.

### RaspberryPiDebugLaunchProvider
Implements: `IDebugLaunchProvider`  
Attribute: `[ExportDebugger(RaspberryPiDebugger.SchemaName)]`

Key methods:
- `CanLaunchAsync(DebugLaunchOptions)` — returns true if active profile is "Raspberry Pi"
- `QueryDebugTargetsAsync(DebugLaunchOptions)` — returns the VsDebugTargetInfo4 list
- `LaunchAsync(DebugLaunchContext)` — called if QueryDebugTargets returns empty (fallback)

### RaspberryPiDebugProfileProvider
Implements: `IDebugProfileProvider` (CPS)  
Provides the list of `RaspberryPiLaunchProfile` instances from the device registry.

### RaspberryPiPropertyPage
Implements: `IPropertyPage` / CPS property page  
Exposes per-profile settings: device dropdown, remote path, environment variables override.
