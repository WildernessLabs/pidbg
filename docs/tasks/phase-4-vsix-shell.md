# Phase 4 — VSIX Shell

Builds the Visual Studio extension skeleton: project file, package entry point, SSH
connection management, Output window integration, gRPC channel factory, build
integration, and the SFTP deployment client. The debug launch provider (F5 integration)
is Phase 5.

Task order:
```
P4.1 (project file) → P4.2 (package) → P4.3 (SSH) ─┐
                                                      ├─▶ P4.6 (gRPC channel) → P4.8 (deploy client)
P4.4 (project props) ──────────────────────────────┘
P4.5 (output window) ──────────────────────────────▶ used by P4.8
P4.7 (publish)       ──────────────────────────────▶ feeds P4.8
```

---

## P4.1 — VSIX Project File and Extension Manifest

**Purpose**: Create the Visual Studio 2022/2026 VSIX project file and `source.extension.vsixmanifest`
that define the extension identity, its VS version requirements, and the NuGet packages it needs.

**Dependencies**: P1.1, P1.2, P1.3

**Files**:
- `Source/VsExtension/PiDbg.Vsix.csproj`
- `Source/VsExtension/source.extension.vsixmanifest`
- `Source/VsExtension/Properties/AssemblyInfo.cs`

**Implementation details**:

`PiDbg.Vsix.csproj`:
```xml
<Project Sdk="Microsoft.VisualStudio.Sdk.Vsix/17.x">
  <!-- Use the VS SDK VSIX project type, not Microsoft.NET.Sdk -->
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <UseWindowsForms>false</UseWindowsForms>
    <RootNamespace>PiDbg.Vsix</RootNamespace>
    <AssemblyName>PiDbg.Vsix</AssemblyName>
    <VsixSourceManifest>source.extension.vsixmanifest</VsixSourceManifest>
    <GeneratePkgDefFile>true</GeneratePkgDefFile>
    <IncludeAssemblyInVSIXContainer>true</IncludeAssemblyInVSIXContainer>
    <!-- Embed vsdbg tarball as a resource for offline provisioning -->
    <AllowUnsafeBlocks>false</AllowUnsafeBlocks>
  </PropertyGroup>

  <ItemGroup>
    <!-- VS SDK -->
    <PackageReference Include="Microsoft.VisualStudio.SDK" Version="17.x" />
    <PackageReference Include="Microsoft.VSSDK.BuildTools" Version="17.x" PrivateAssets="all" />
    <!-- SSH.NET -->
    <PackageReference Include="SSH.NET" />
    <!-- gRPC client -->
    <PackageReference Include="Grpc.Net.Client" />
    <PackageReference Include="Google.Protobuf" />
  </ItemGroup>

  <ItemGroup>
    <!-- Reference the shared contracts project -->
    <ProjectReference Include="..\Meadow.Daemon.Contracts\Meadow.Daemon.Contracts.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Embedded resources for offline provisioning -->
    <EmbeddedResource Include="Resources\detect.sh" />
    <EmbeddedResource Include="Resources\setup-meadow.sh" />
    <EmbeddedResource Include="Resources\meadow-daemon.service.template" />
    <!-- vsdbg tarball embedded for offline install -->
    <EmbeddedResource Include="Resources\vsdbg-linux-arm64.tar.gz"
                      Condition="Exists('Resources\vsdbg-linux-arm64.tar.gz')" />
  </ItemGroup>
</Project>
```

`source.extension.vsixmanifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011">
  <Metadata>
    <Identity Id="PiDbg.Vsix" Version="1.0.0" Language="en-US"
              Publisher="Wilderness Labs" />
    <DisplayName>PiDbg — Raspberry Pi Remote Debugger</DisplayName>
    <Description>Visual Studio remote debugging for .NET 10 apps on Raspberry Pi ARM64.</Description>
    <Tags>raspberry-pi, remote-debug, arm64, iot, dotnet</Tags>
    <Icon>Resources\pidbg-icon.png</Icon>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Community" Version="[17.0,)" />
    <InstallationTarget Id="Microsoft.VisualStudio.Professional" Version="[17.0,)" />
    <InstallationTarget Id="Microsoft.VisualStudio.Enterprise" Version="[17.0,)" />
  </Installation>
  <Prerequisites>
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor" Version="[17.0,)"
                  DisplayName="Visual Studio core editor" />
  </Prerequisites>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.VsPackage" d:Source="Project" d:ProjectName="%CurrentProject%"
           Path="|%CurrentProject%;PkgdefProjectOutputGroup|" />
    <Asset Type="Microsoft.VisualStudio.MefComponent" d:Source="Project" d:ProjectName="%CurrentProject%"
           Path="|%CurrentProject%|" />
  </Assets>
</PackageManifest>
```

**Edge cases**:
- `Microsoft.VisualStudio.SDK` version must match the minimum VS version. For VS 2022
  (17.x), use `17.6.x` or later. Check the VSSDK changelog for breaking changes.
- `net10.0-windows` is required for WPF property pages and VS interop. The `-windows`
  TFM adds Windows-specific APIs.
- SSH.NET and gRPC packages must be included in the VSIX container
  (`IncludeAssemblyInVSIXContainer=true`). Otherwise they won't be available at runtime.
- The `Resources\` directory must exist with a placeholder icon before the project builds.
  Create a 32×32 PNG placeholder if the real icon is not available yet.
- `vsdbg-linux-arm64.tar.gz` is a large embedded resource (~55 MB). Only embed it if
  it exists (`Condition="Exists(...)"`) so the project builds without it during
  development. The offline install path still works without it (just unavailable offline).

**Testing requirements**:
- `dotnet build Source/VsExtension/PiDbg.Vsix.csproj` exits 0
- The `.vsix` file is produced in `bin/` after build
- Open the produced `.vsix` in a zip viewer and verify it contains the extension DLL and
  all embedded resources

**Definition of done**:
- [ ] `.csproj` uses VS SDK VSIX project type targeting `net10.0-windows`
- [ ] `source.extension.vsixmanifest` present with correct identity and installation targets
- [ ] SSH.NET, Grpc.Net.Client, Google.Protobuf referenced
- [ ] `Meadow.Daemon.Contracts` project reference included
- [ ] Embedded resources: `detect.sh`, `setup-meadow.sh`, service template
- [ ] `dotnet build` produces a `.vsix` file

---

## P4.2 — AsyncPackage Entry Point

**Purpose**: Create the VS package class that initialises PiDbg when Visual Studio loads
the extension, registers all commands, and exposes the service container to other
extension components.

**Dependencies**: P4.1

**Files**:
- `Source/VsExtension/PiDbgPackage.cs`
- `Source/VsExtension/PiDbgPackage.vsct`

**Implementation details**:

```csharp
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(PackageGuidString)]
[ProvideAutoLoad(UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
public sealed class PiDbgPackage : AsyncPackage
{
    public const string PackageGuidString = "A1B2C3D4-..."; // generate a real GUID

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // Switch to UI thread to register commands
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        // Register services into the VS service container
        // (services used by other VSIX components)
        AddService(typeof(ISshConnectionManager), CreateSshConnectionManagerAsync, true);
        AddService(typeof(IOutputWindowService), CreateOutputWindowServiceAsync, true);

        // Register commands (P4.3 and onwards)
        // await RepairConnectionCommand.InitializeAsync(this);
        // await RunDiagnosticsCommand.InitializeAsync(this);
        // await UninstallCommand.InitializeAsync(this);
    }

    private async Task<object> CreateSshConnectionManagerAsync(
        IAsyncServiceContainer container, CancellationToken ct, Type serviceType)
    {
        await TaskScheduler.Default;  // switch to background thread
        return new SshConnectionManager(this);
    }

    private async Task<object> CreateOutputWindowServiceAsync(
        IAsyncServiceContainer container, CancellationToken ct, Type serviceType)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        return new OutputWindowService(this);
    }
}
```

`PiDbgPackage.vsct` must define:
- Menu group `PiDbgGroup` under the `Tools` menu
- Commands:
  - `PiDbg.RepairConnection` (ID: 0x0101)
  - `PiDbg.RunDiagnostics` (ID: 0x0102)
  - `PiDbg.UninstallFromDevice` (ID: 0x0103)
  - `PiDbg.ExportDiagnosticBundle` (ID: 0x0104)

Command visibility: only visible when a solution with `PiDbgHost` property is loaded.
Use `DynamicVisibility` and a custom `UIContext` for this.

**Edge cases**:
- `ProvideAutoLoad` with `BackgroundLoad` flag means the package loads on a background
  thread. All VS UI operations must be preceded by `SwitchToMainThreadAsync`.
- `AddService` with `promote=true` makes the service available to all packages, not just
  this one. This is needed for the debug launch provider which runs in a different MEF
  component.
- The `PackageGuidString` must be a new, unique GUID. Generate one with `Guid.NewGuid()`
  or Visual Studio's "Create GUID" tool. Never reuse a GUID from another extension.

**Testing requirements**:
- Install the extension into an experimental VS instance (`/RootSuffix Exp`)
- Verify "PiDbg" menu appears under `Tools`
- Verify all 4 commands are listed (greyed out is fine for now)
- Verify the package loads without errors in the VS Activity Log
  (`%APPDATA%\Microsoft\VisualStudio\17.0Exp\ActivityLog.xml`)

**Definition of done**:
- [ ] `PiDbgPackage` inherits `AsyncPackage`
- [ ] `ProvideAutoLoad` with `BackgroundLoading` on `SolutionExists` UIContext
- [ ] `InitializeAsync` switches to main thread before registering commands
- [ ] Service container registers `ISshConnectionManager` and `IOutputWindowService`
- [ ] `.vsct` file defines all 4 commands in the Tools menu
- [ ] Extension loads without errors in experimental VS instance

---

## P4.3 — SSH Connection Manager

**Purpose**: Provide a single, reusable SSH connection abstraction over SSH.NET that
manages connection lifecycle, reconnection, port forwarding, and credential storage for
all PiDbg operations.

**Dependencies**: P4.2

**Files**:
- `Source/VsExtension/Infrastructure/SshConnectionManager.cs`
- `Source/VsExtension/Infrastructure/SshSession.cs`
- `Source/VsExtension/Infrastructure/SshConnectionConfig.cs`
- `Source/VsExtension/Infrastructure/ISshConnectionManager.cs`

**Implementation details**:

`SshConnectionConfig`:
```csharp
public sealed record SshConnectionConfig(
    string Host,
    string User,
    int    Port       = 22,
    string? KeyFile   = null,    // path to private key file; null = use password
    string? Password  = null     // null when using key auth
);
```

`ISshConnectionManager`:
```csharp
public interface ISshConnectionManager
{
    Task<SshSession> ConnectAsync(SshConnectionConfig config, CancellationToken ct);
    void Disconnect(string host);
    SshSession? GetActiveSession(string host);
}
```

`SshSession`:
```csharp
public sealed class SshSession : IDisposable
{
    public SshClient  Ssh  { get; }
    public SftpClient Sftp { get; }
    public string Host     { get; }

    // Execute a remote command, returns stdout
    public async Task<(int ExitCode, string Stdout, string Stderr)>
        ExecuteAsync(string command, CancellationToken ct)
    {
        using var cmd = Ssh.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(30);
        var result = await Task.Run(() => cmd.Execute(), ct);
        return (cmd.ExitStatus, cmd.Result, cmd.Error);
    }

    // Upload a file via SFTP
    public async Task UploadFileAsync(Stream source, string remotePath,
        IProgress<long>? progress, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(remotePath)!.Replace('\\', '/');
        // Ensure remote directory exists
        EnsureRemoteDir(dir);

        await Task.Run(() =>
        {
            Sftp.UploadFile(source, remotePath, canOverride: true, uploadCallback: uploaded =>
                progress?.Report(uploaded));
        }, ct);
    }

    // Open a port-forward tunnel: returns the assigned local port
    public async Task<(ForwardedPortLocal Port, int LocalPort)>
        OpenTunnelAsync(int remotePort, CancellationToken ct)
    {
        var fwd = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)remotePort);
        Ssh.AddForwardedPort(fwd);
        fwd.Start();
        await Task.Delay(100, ct); // small delay for the listener to bind
        return (fwd, (int)fwd.BoundPort);
    }

    private void EnsureRemoteDir(string remotePath)
    {
        // Walk the path components, create each directory if missing
        // Uses SftpClient.GetAttributes to check existence; create if absent
        var parts = remotePath.TrimStart('/').Split('/');
        var current = "";
        foreach (var part in parts)
        {
            current += "/" + part;
            try { Sftp.GetAttributes(current); }
            catch (SftpPathNotFoundException) { Sftp.CreateDirectory(current); }
        }
    }

    public void Dispose()
    {
        Sftp.Dispose();
        Ssh.Dispose();
    }
}
```

`SshConnectionManager.ConnectAsync`:
1. Check `_sessions` cache — if session exists and `IsConnected`, return it
2. Build `AuthenticationMethod[]`:
   - If `KeyFile` is set: `PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(keyFile))`
   - Else: `PasswordAuthenticationMethod(user, password)`
3. Create `ConnectionInfo(host, port, user, authMethods)`
4. Create `SshClient` and `SftpClient` with same `ConnectionInfo`
5. Call `sshClient.Connect()` and `sftpClient.Connect()`
6. Wrap in `SshSession`, cache by `host`, return

Retry on connect: 3 attempts, 2s backoff, wrapping `SocketException` and `SshException`.

**Edge cases**:
- `SftpClient.UploadFile` is synchronous. Wrapping it in `Task.Run` keeps the VS UI
  thread responsive. The `CancellationToken` cancels the `Task.Run` wrapper, not the
  underlying SFTP operation. For large uploads, implement chunked upload with periodic
  cancellation checks.
- `fwd.BoundPort` is `uint` and may be 0 if the port hasn't bound yet. The 100ms delay
  is a heuristic. A more robust approach polls `BoundPort != 0` with a short timeout.
- `SshSession` caching: store by `host:port`. If `host` changes user or credentials,
  the cached session must be invalidated.
- `EnsureRemoteDir` makes one SFTP call per directory level. For deeply nested paths
  this can be slow. Cache successfully-created directories within the session.
- `ConnectionInfo` reuse for both `SshClient` and `SftpClient` means they share
  authentication state. This is correct per SSH.NET documentation.

**Testing requirements**:
- Integration test: `ConnectAsync` against a real Pi (or mock SSH server) succeeds
- Integration test: `ExecuteAsync("echo hello")` returns `(0, "hello\n", "")`
- Integration test: `UploadFileAsync` uploads a test file and verifies content on device
- Integration test: `OpenTunnelAsync` returns a local port that forwards to remote
- Unit test: reconnect logic retries 3 times before throwing

**Definition of done**:
- [ ] `ISshConnectionManager` interface defined
- [ ] `SshSession` wraps `SshClient` + `SftpClient` with helper methods
- [ ] `ConnectAsync` supports both password and key-file auth
- [ ] Session caching by host
- [ ] Retry on connect (3 attempts, 2s backoff)
- [ ] `OpenTunnelAsync` returns OS-assigned local port
- [ ] `EnsureRemoteDir` creates intermediate directories
- [ ] Dispose pattern correctly closes both clients

---

## P4.4 — Project System Integration

**Purpose**: Read PiDbg-specific MSBuild properties from the loaded project file so the
extension knows the target host, user, and SSH configuration without hardcoding.

**Dependencies**: P4.2

**Files**:
- `Source/VsExtension/ProjectSystem/PiDbgProjectProperties.cs`
- `Source/VsExtension/ProjectSystem/IPiDbgProjectProperties.cs`

**Implementation details**:

The following MSBuild properties are read from the active project:
```
PiDbgHost        — hostname or IP of the Raspberry Pi (required)
PiDbgUser        — SSH user (default: "pi")
PiDbgSshPort     — SSH port (default: 22)
PiDbgSshKeyFile  — path to SSH private key (optional; empty = use credential store)
PiDbgAppName     — app name for deployment (default: project AssemblyName)
```

Reading MSBuild properties from a VS extension:
```csharp
public sealed class PiDbgProjectProperties : IPiDbgProjectProperties
{
    private readonly IVsBuildPropertyStorage _storage;
    private readonly IVsHierarchy _hierarchy;

    public async Task<SshConnectionConfig?> GetConnectionConfigAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        _storage.GetPropertyValue("PiDbgHost", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var host);
        if (string.IsNullOrWhiteSpace(host)) return null;

        _storage.GetPropertyValue("PiDbgUser", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var user);
        _storage.GetPropertyValue("PiDbgSshPort", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var portStr);
        _storage.GetPropertyValue("PiDbgSshKeyFile", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var keyFile);

        return new SshConnectionConfig(
            Host:    host,
            User:    string.IsNullOrEmpty(user) ? "pi" : user,
            Port:    int.TryParse(portStr, out var p) ? p : 22,
            KeyFile: string.IsNullOrEmpty(keyFile) ? null : keyFile
        );
    }

    public async Task<string> GetAppNameAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _storage.GetPropertyValue("PiDbgAppName", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var appName);
        if (!string.IsNullOrEmpty(appName)) return appName;

        // Fall back to AssemblyName
        _storage.GetPropertyValue("AssemblyName", null,
            (uint)_PersistStorageType.PST_PROJECT_FILE, out var assemblyName);
        return assemblyName ?? "MyApp";
    }
}
```

Obtain `IVsBuildPropertyStorage` from the active project hierarchy:
```csharp
var dte = (DTE2)await package.GetServiceAsync(typeof(DTE));
var project = dte.Solution.StartupProjects?[0]; // or active project
// Cast through COM interop to IVsBuildPropertyStorage
```

Or via `IVsSolution` + `IVsHierarchy`:
```csharp
var solution = await package.GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
solution.GetProjectOfUniqueName(project.UniqueName, out var hier);
var storage = hier as IVsBuildPropertyStorage;
```

**Edge cases**:
- `GetPropertyValue` returns `S_OK` but empty string if the property is not set in the
  `.csproj`. Always check for empty/null and apply defaults.
- `PiDbgHost` is mandatory. If not set, the extension returns a user-visible error:
  "Set PiDbgHost in your project properties to the Raspberry Pi hostname or IP."
- MSBuild property reads must happen on the UI thread (COM requirement) — always call
  `SwitchToMainThreadAsync` first.
- Properties are read at each F5 press, not cached. This allows changing the target
  host without reloading the solution.

**Testing requirements**:
- Unit test: returns `null` when `PiDbgHost` is empty
- Unit test: returns correct `SshConnectionConfig` from populated properties
- Unit test: `PiDbgAppName` falls back to `AssemblyName` when not set
- Manual test: set `PiDbgHost=mypi.local` in `.csproj`, verify it is read correctly

**Definition of done**:
- [ ] Reads all 5 MSBuild properties listed above
- [ ] Returns `null` connection config when `PiDbgHost` is not set
- [ ] Applies correct defaults for optional properties
- [ ] All reads happen on the UI thread
- [ ] Unit tests cover null/empty/populated cases

---

## P4.5 — Output Window Panes

**Purpose**: Create dedicated output window panes for deployment/debug lifecycle events
and provisioning events, so the developer can see structured progress without switching
to another tool.

**Dependencies**: P4.2

**Files**:
- `Source/VsExtension/UI/OutputWindowService.cs`
- `Source/VsExtension/UI/IOutputWindowService.cs`

**Implementation details**:

```csharp
public interface IOutputWindowService
{
    void Write(OutputPane pane, string message);
    void WriteLine(OutputPane pane, string message);
    void WriteError(OutputPane pane, string message);
    void WriteWarning(OutputPane pane, string message);
    void Clear(OutputPane pane);
    void Activate(OutputPane pane);
}

public enum OutputPane { PiDbg, Provisioning }
```

```csharp
public sealed class OutputWindowService : IOutputWindowService
{
    private readonly IVsOutputWindowPane _pidbgPane;
    private readonly IVsOutputWindowPane _provisionPane;

    public OutputWindowService(AsyncPackage package)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var outputWindow = (IVsOutputWindow)package.GetService(typeof(SVsOutputWindow))!;

        _pidbgPane     = GetOrCreatePane(outputWindow, PiDbgPaneGuid,     "PiDbg");
        _provisionPane = GetOrCreatePane(outputWindow, ProvisionPaneGuid, "PiDbg Provisioning");
    }

    public void WriteLine(OutputPane pane, string message)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane(pane).OutputStringThreadSafe(
            $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    public void WriteError(OutputPane pane, string message)
        => WriteLine(pane, $"ERROR: {message}");

    public void WriteWarning(OutputPane pane, string message)
        => WriteLine(pane, $"WARN:  {message}");

    public void Activate(OutputPane pane)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        GetPane(pane).Activate();
    }

    private static IVsOutputWindowPane GetOrCreatePane(
        IVsOutputWindow window, Guid paneGuid, string paneName)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        window.GetPane(ref paneGuid, out var pane);
        if (pane is null)
        {
            window.CreatePane(ref paneGuid, paneName,
                fInitVisible: 1, fClearWithSolution: 0);
            window.GetPane(ref paneGuid, out pane);
        }
        return pane!;
    }

    private static readonly Guid PiDbgPaneGuid     = new("B1C2D3E4-...");
    private static readonly Guid ProvisionPaneGuid = new("C2D3E4F5-...");
}
```

`OutputStringThreadSafe` is the correct method for writing from background threads
(which all provisioning and deployment work runs on).

**Edge cases**:
- `ThreadHelper.ThrowIfNotOnUIThread()` must NOT be in `WriteLine` because it is
  called from background threads via `OutputStringThreadSafe`. Remove it from `WriteLine`.
  Only `GetOrCreatePane`, `Activate`, and the constructor require the UI thread.
- `fClearWithSolution = 0` means the pane is NOT cleared when a new solution is opened.
  This preserves provisioning history across solution loads, which is the desired behaviour.
- Both `Guid` values must be real, unique GUIDs. Generate them once and hardcode.

**Testing requirements**:
- Manual test: after installing the extension, open the Output window and see both
  "PiDbg" and "PiDbg Provisioning" panes listed
- Manual test: `WriteLine` from a background thread does not throw or deadlock
- Unit test (mock): `WriteError` prefixes the message with "ERROR: "

**Definition of done**:
- [ ] Two panes: "PiDbg" and "PiDbg Provisioning"
- [ ] Each pane has a stable GUID
- [ ] `OutputStringThreadSafe` used for background-thread writes
- [ ] `Activate()` brings the pane to the foreground
- [ ] Constructor creates panes if they don't exist (idempotent)

---

## P4.6 — gRPC Channel Factory

**Purpose**: Create and cache a gRPC channel that routes through an SSH port-forward
tunnel so the VSIX can call daemon RPCs without exposing the gRPC port externally.

**Dependencies**: P4.3

**Files**:
- `Source/VsExtension/Infrastructure/GrpcChannelFactory.cs`
- `Source/VsExtension/Infrastructure/IGrpcChannelFactory.cs`

**Implementation details**:

```csharp
public interface IGrpcChannelFactory
{
    Task<GrpcChannel> GetOrCreateChannelAsync(SshSession session, CancellationToken ct);
    void DisposeChannel(string host);
}

public sealed class GrpcChannelFactory : IGrpcChannelFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, (GrpcChannel Channel, ForwardedPortLocal Tunnel)>
        _channels = new();

    public async Task<GrpcChannel> GetOrCreateChannelAsync(
        SshSession session, CancellationToken ct)
    {
        if (_channels.TryGetValue(session.Host, out var existing)
            && existing.Tunnel.IsStarted)
            return existing.Channel;

        // Open SSH tunnel: remote 50051 → local (OS-assigned port)
        var (tunnel, localPort) = await session.OpenTunnelAsync(50051, ct);

        // Create gRPC channel pointing at the tunnel's local port
        // Use SocketsHttpHandler for .NET (not WinHttpHandler)
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingDelay     = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout   = TimeSpan.FromSeconds(10),
            ConnectTimeout         = TimeSpan.FromSeconds(10),
        };

        var channel = GrpcChannel.ForAddress(
            $"http://127.0.0.1:{localPort}",
            new GrpcChannelOptions
            {
                HttpHandler             = handler,
                MaxReceiveMessageSize   = 64 * 1024 * 1024,
                MaxSendMessageSize      = 64 * 1024 * 1024,
                ThrowOperationCanceledOnCancellation = true,
            });

        _channels[session.Host] = (channel, tunnel);

        // Verify connectivity
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        await client.PingAsync(new PingRequest(), cancellationToken: ct);

        return channel;
    }

    public void DisposeChannel(string host)
    {
        if (_channels.TryRemove(host, out var entry))
        {
            entry.Tunnel.Stop();
            entry.Channel.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var (channel, tunnel) in _channels.Values)
        {
            tunnel.Stop();
            channel.Dispose();
        }
        _channels.Clear();
    }
}
```

**Edge cases**:
- `WinHttpHandler` does not support `HTTP/2` without TLS on Windows. Use
  `SocketsHttpHandler` instead — it supports cleartext HTTP/2 (`h2c`).
- The Ping call after channel creation verifies the tunnel is working. If Ping fails
  with a `RpcException`, the tunnel or daemon is not ready. Surface this as a
  provisioning error, not an internal error.
- `EnableMultipleHttp2Connections = true` allows multiple concurrent gRPC streams
  (e.g. `StreamLogs` and `StreamOutput` simultaneously) without blocking each other.
- If the SSH tunnel drops while a gRPC call is in flight, the call fails with
  `RpcException(StatusCode.Unavailable)`. The VSIX should detect this and trigger
  reconnect logic.

**Testing requirements**:
- Integration test: `GetOrCreateChannelAsync` with a live Pi returns a working channel
- Integration test: `Ping` through the channel succeeds
- Integration test: channel is reused on second call (no new tunnel opened)
- Integration test: after `DisposeChannel`, a new call creates a new tunnel
- Unit test: `SocketsHttpHandler` used (not `WinHttpHandler`)

**Definition of done**:
- [ ] Uses `SocketsHttpHandler` (not `WinHttpHandler`)
- [ ] Tunnel opened on OS-assigned port
- [ ] Channel cached by host, reused on subsequent calls
- [ ] Ping called after channel creation to verify connectivity
- [ ] `Dispose` closes all tunnels and channels
- [ ] `MaxReceiveMessageSize = 64 MB`

---

## P4.7 — Dotnet Publish Integration

**Purpose**: Run `dotnet publish` on the target project with the correct flags for
Pi deployment, collect all output files, compute their SHA-256 hashes, and build a
`DeploymentManifest`.

**Dependencies**: P4.4, P1.3

**Files**:
- `Source/VsExtension/Build/PublishService.cs`
- `Source/VsExtension/Build/PublishResult.cs`

**Implementation details**:

```csharp
public sealed class PublishResult
{
    public string            PublishDir   { get; init; } = "";
    public DeploymentManifest Manifest    { get; init; } = new();
    public TimeSpan           Duration    { get; init; }
}

public sealed class PublishService
{
    private readonly IOutputWindowService _output;

    public async Task<PublishResult> PublishAsync(
        string projectPath, string appName,
        IProgress<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var publishDir = Path.Combine(Path.GetTempPath(), "pidbg-publish", appName);
        if (Directory.Exists(publishDir)) Directory.Delete(publishDir, recursive: true);
        Directory.CreateDirectory(publishDir);

        // Build arguments
        var args = new[]
        {
            "publish",
            $"\"{projectPath}\"",
            "-c", "Debug",
            "-r", "linux-arm64",
            "--no-self-contained",  // framework-dependent: smaller transfer
            "--output", $"\"{publishDir}\"",
            "/p:Optimize=false",
            "/p:DebugType=portable",
            "/p:EmbedAllSources=true",
            "/p:Deterministic=true",
        };

        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", string.Join(" ", args))
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            }
        };

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                _output.WriteLine(OutputPane.PiDbg, e.Data);
                progress.Report(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                _output.WriteError(OutputPane.PiDbg, e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            throw new PublishException($"dotnet publish failed with exit code {process.ExitCode}");

        var manifest = await BuildManifestAsync(publishDir, appName, ct);
        return new PublishResult
        {
            PublishDir = publishDir,
            Manifest   = manifest,
            Duration   = sw.Elapsed,
        };
    }

    private static async Task<DeploymentManifest> BuildManifestAsync(
        string publishDir, string appName, CancellationToken ct)
    {
        var files = Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories);
        var entries = new List<FileEntry>(files.Length);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(publishDir, file)
                                   .Replace('\\', '/');
            var sha256 = await ComputeSha256Async(file, ct);
            entries.Add(new FileEntry
            {
                Path     = relativePath,
                Sha256   = sha256,
                SizeBytes = new FileInfo(file).Length,
                Role     = InferRole(relativePath, appName),
            });
        }

        var deploymentId = NewUlid();
        var manifest = new DeploymentManifest
        {
            ManifestVersion = 1,
            DeploymentId    = deploymentId,
            Slot            = DeploymentSlot.Debug,
            VersionLabel    = deploymentId,
            EntryPoint      = $"{appName}.dll",
        };
        manifest.Files.AddRange(entries);
        manifest.ManifestSha256 = ComputeManifestHash(manifest);
        return manifest;
    }

    private static string InferRole(string relativePath, string appName) => relativePath switch
    {
        var p when p.EndsWith(".dll") && p.StartsWith(appName) => "entrypoint",
        var p when p.EndsWith(".pdb")                           => "symbols",
        var p when p.EndsWith(".json")                          => "config",
        _                                                       => "runtime"
    };
}
```

**Edge cases**:
- `--no-self-contained` requires the Pi to have the .NET 10 runtime installed.
  If the daemon is self-contained, the runtime is already available.
  However, the *app* being debugged uses `--no-self-contained` (framework-dependent)
  to produce a small deployment package. The runtime on the Pi comes from the daemon's
  self-contained publish, or from a separate runtime install.
  **Decision**: document that the app project must target `net10.0` and the Pi
  must have .NET 10 runtime. The provisioning system verifies this.
- `EmbedAllSources=true` embeds source file content in the PDB. This eliminates
  source mapping problems in the debugger but increases PDB size. Accept the tradeoff.
- `process.WaitForExitAsync(ct)` with `CancellationToken` — if cancelled, the process
  is left running. Add `ct.Register(() => process.Kill(entire: true))` to kill on cancel.
- `NewUlid()` must be implemented (either via a NuGet package or inline implementation).

**Testing requirements**:
- Integration test: publish a minimal .NET app and verify the output directory has
  the expected files
- Unit test: `BuildManifestAsync` correctly computes SHA-256 for each file
- Unit test: `InferRole` correctly identifies `.dll` as entrypoint, `.pdb` as symbols
- Unit test: `ManifestSha256` field is set on the returned manifest

**Definition of done**:
- [ ] `dotnet publish` invoked with `-c Debug -r linux-arm64 --no-self-contained`
- [ ] `EmbedAllSources=true`, `Optimize=false`, `Deterministic=true` passed as `/p:`
- [ ] All publish output files included in manifest with SHA-256 and size
- [ ] `ManifestSha256` field computed and set
- [ ] Process killed on `CancellationToken` cancellation
- [ ] Output streamed to PiDbg Output pane

---

## P4.8 — SFTP Deployment Client

**Purpose**: Transfer publish output files to the daemon's staging directory via SFTP,
using 4 parallel connections, the delta transfer protocol (upload only changed files),
and progress reporting throughout.

**Dependencies**: P4.3, P4.6, P4.7, P1.3

**Files**:
- `Source/VsExtension/Deploy/SftpDeploymentClient.cs`

**Implementation details**:

```csharp
public sealed class SftpDeploymentClient
{
    private readonly SshSession   _session;
    private readonly GrpcChannel  _channel;
    private readonly IOutputWindowService _output;

    public async Task DeployAsync(
        string appName, string publishDir, DeploymentManifest manifest,
        IProgress<DeploymentProgress> progress, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(_channel);

        // 1. Begin deployment (with delta base)
        _output.WriteLine(OutputPane.PiDbg, $"Beginning deployment of {appName}...");
        var beginResp = await client.BeginDeploymentAsync(new BeginDeploymentRequest
        {
            AppName  = appName,
            Manifest = manifest,
            Slot     = DeploymentSlot.Debug,
            DeltaBase = "debug",  // hard-link unchanged from previous debug slot
        }, cancellationToken: ct);

        var deploymentId = beginResp.DeploymentId;
        var filesNeeded  = beginResp.FilesNeeded.ToHashSet();
        var stagingDir   = beginResp.StagingDir;

        _output.WriteLine(OutputPane.PiDbg,
            $"Uploading {filesNeeded.Count}/{manifest.Files.Count} files " +
            $"({manifest.Files.Count - filesNeeded.Count} unchanged)");

        // 2. Upload only the files that are needed
        var filesToUpload = manifest.Files.Where(f => filesNeeded.Contains(f.Path)).ToList();
        var totalBytes    = filesToUpload.Sum(f => f.SizeBytes);
        var uploadedBytes = 0L;

        try
        {
            // 4 parallel SFTP uploads
            await Parallel.ForEachAsync(filesToUpload,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (entry, innerCt) =>
                {
                    var localPath  = Path.Combine(publishDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                    var remotePath = $"{stagingDir}/{entry.Path}";
                    await using var stream = File.OpenRead(localPath);

                    await _session.UploadFileAsync(stream, remotePath,
                        new Progress<long>(bytes =>
                        {
                            Interlocked.Add(ref uploadedBytes, bytes);
                            progress.Report(new DeploymentProgress(
                                Phase: "Uploading",
                                BytesSent: uploadedBytes,
                                TotalBytes: totalBytes));
                        }),
                        innerCt);
                });

            // 3. Commit
            progress.Report(new DeploymentProgress("Verifying", uploadedBytes, totalBytes));
            var commitResp = await client.CommitDeploymentAsync(
                new CommitDeploymentRequest { DeploymentId = deploymentId },
                cancellationToken: ct);

            if (!commitResp.Success)
            {
                var failures = string.Join(", ", commitResp.Failures.Select(f => f.Path));
                throw new DeploymentException(
                    $"Deployment verification failed for: {failures}. " +
                    $"{commitResp.ErrorMessage}");
            }

            _output.WriteLine(OutputPane.PiDbg, "Deployment complete.");
        }
        catch when (!ct.IsCancellationRequested)
        {
            // Abort the deployment on non-cancellation errors
            try
            {
                await client.AbortDeploymentAsync(
                    new AbortDeploymentRequest { DeploymentId = deploymentId },
                    cancellationToken: CancellationToken.None);
            }
            catch { /* best-effort abort */ }
            throw;
        }
        catch (OperationCanceledException)
        {
            // Also abort on cancellation
            _ = client.AbortDeploymentAsync(
                new AbortDeploymentRequest { DeploymentId = deploymentId },
                cancellationToken: CancellationToken.None);
            throw;
        }
    }
}

public record DeploymentProgress(string Phase, long BytesSent, long TotalBytes)
{
    public int PercentComplete => TotalBytes == 0 ? 100 : (int)(BytesSent * 100 / TotalBytes);
}
```

**Edge cases**:
- `Interlocked.Add` for the progress counter is required because 4 parallel uploads
  update it concurrently. Plain `+=` would be a data race.
- `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 4` creates at most 4 concurrent
  SFTP operations. SSH.NET supports multiple concurrent requests on one connection (SFTP
  supports up to 64 by default). 4 is conservative but reliable on SD card I/O.
- The `stagingDir` path returned from `BeginDeployment` is an absolute Linux path
  (e.g. `/opt/meadow/apps/MyApp/staging`). Use it directly as the remote path prefix
  without any client-side path manipulation.
- Abort on failure/cancellation uses `CancellationToken.None` — even if the deployment
  was cancelled, we still want to abort the server-side state.

**Testing requirements**:
- Integration test: deploy a 5-file publish output, verify all files appear in staging
- Integration test: deploy where 4/5 files are unchanged → only 1 uploaded
- Integration test: cancel mid-deploy → `AbortDeployment` called on server
- Integration test: deploy with a file SHA-256 mismatch → commit returns failure

**Definition of done**:
- [ ] Calls `BeginDeployment` with `deltaBase="debug"`
- [ ] Uploads only `filesNeeded` (not all files)
- [ ] 4 parallel SFTP uploads
- [ ] Progress reported as bytes uploaded
- [ ] Commits on success, aborts on failure or cancellation
- [ ] `CommitDeploymentResponse.Success=false` surfaced as `DeploymentException`
