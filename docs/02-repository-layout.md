# PiDbg — Repository Layout

## Directory Tree

```
pidbg/
│
├── pidbg.slnx                          # Solution file
│
├── src/
│   │
│   ├── PiDbg.Vsix/                    # Visual Studio 2026 extension
│   │   ├── PiDbg.Vsix.csproj
│   │   ├── source.extension.vsixmanifest
│   │   ├── VSPackage.cs               # AsyncPackage entry point
│   │   ├── Commands/
│   │   │   ├── AddDeviceCommand.cs
│   │   │   ├── ManageDevicesCommand.cs
│   │   │   └── ShowOutputCommand.cs
│   │   ├── Debug/
│   │   │   ├── RaspberryPiDebugLaunchProvider.cs
│   │   │   ├── RaspberryPiDebugProfileProvider.cs
│   │   │   ├── RaspberryPiLaunchProfile.cs
│   │   │   └── DebugSessionOrchestrator.cs
│   │   ├── UI/
│   │   │   ├── DeviceManagerWindow.cs         # ToolWindowPane
│   │   │   ├── DeviceManagerWindowControl.xaml
│   │   │   ├── DeviceManagerWindowControl.xaml.cs
│   │   │   ├── PropertyPages/
│   │   │   │   ├── RaspberryPiPropertyPage.cs
│   │   │   │   └── RaspberryPiPropertyPageViewModel.cs
│   │   │   └── ViewModels/
│   │   │       ├── DeviceListViewModel.cs
│   │   │       └── DeviceItemViewModel.cs
│   │   ├── Services/
│   │   │   ├── OutputWindowService.cs
│   │   │   └── VsProjectService.cs
│   │   └── Resources/
│   │       ├── VSPackage.resx
│   │       └── raspberrypi.png
│   │
│   ├── PiDbg.Agent/                   # Pi-side daemon
│   │   ├── PiDbg.Agent.csproj
│   │   ├── Program.cs                 # Minimal hosting entry point
│   │   ├── Services/
│   │   │   ├── AgentGrpcService.cs    # Implements proto service contract
│   │   │   ├── DeploymentManager.cs
│   │   │   ├── ProcessLifecycleService.cs
│   │   │   ├── VsdbgManager.cs
│   │   │   ├── VsdbgInstaller.cs
│   │   │   ├── MeadowDaemonClient.cs
│   │   │   └── AgentHealthService.cs
│   │   ├── Models/
│   │   │   ├── DeploymentRecord.cs
│   │   │   └── DebugSessionRecord.cs
│   │   └── systemd/
│   │       └── pidbg-agent.service.template
│   │
│   ├── PiDbg.Contracts/               # Shared protobuf + generated code
│   │   ├── PiDbg.Contracts.csproj
│   │   ├── proto/
│   │   │   ├── debug_agent.proto      # Main service definition
│   │   │   ├── deployment.proto       # Deployment types
│   │   │   ├── session.proto          # Debug session types
│   │   │   └── common.proto           # Shared types
│   │   └── Extensions/
│   │       └── ProtoExtensions.cs
│   │
│   ├── PiDbg.Transport/               # SSH.NET abstraction
│   │   ├── PiDbg.Transport.csproj
│   │   ├── SshConnectionManager.cs
│   │   ├── SshPortForwardingManager.cs
│   │   ├── SftpTransferService.cs
│   │   ├── SshDeviceProber.cs         # Validate device OS, arch, .NET version
│   │   └── Models/
│   │       ├── SshConnectionOptions.cs
│   │       ├── ForwardedPort.cs
│   │       └── TransferProgress.cs
│   │
│   ├── PiDbg.Deployment/              # Deploy packager + orchestration
│   │   ├── PiDbg.Deployment.csproj
│   │   ├── DeploymentPackager.cs      # Reads dotnet publish output, builds manifest
│   │   ├── ManifestBuilder.cs         # SHA-256 manifest generation
│   │   ├── DeploymentService.cs       # Orchestrates package → transfer → commit
│   │   ├── DeltaCalculator.cs         # Phase 2: only transfer changed files
│   │   └── Models/
│   │       ├── DeploymentPackage.cs
│   │       ├── DeploymentManifest.cs
│   │       └── FileEntry.cs
│   │
│   ├── PiDbg.DeviceManagement/        # Device registry + discovery
│   │   ├── PiDbg.DeviceManagement.csproj
│   │   ├── DeviceRegistry.cs          # Persistent JSON store
│   │   ├── DeviceDiscoveryService.cs  # mDNS / manual add
│   │   ├── DeviceConnectionFactory.cs
│   │   └── Models/
│   │       ├── DeviceRecord.cs        # Immutable record
│   │       ├── DeviceCredentials.cs
│   │       └── DeviceCapabilities.cs  # dotnet version, arch, agent version
│   │
│   └── PiDbg.Shared/                  # Cross-cutting constants + utilities
│       ├── PiDbg.Shared.csproj
│       ├── Constants.cs
│       ├── PiPaths.cs                 # Remote path constants
│       └── Retry/
│           └── RetryPolicy.cs
│
├── tests/
│   │
│   ├── PiDbg.Integration.Tests/       # End-to-end tests against real or mock Pi
│   │   ├── PiDbg.Integration.Tests.csproj
│   │   ├── Infrastructure/
│   │   │   ├── PiFixture.cs           # Manages real/mock Pi connection
│   │   │   └── AgentFixture.cs
│   │   ├── DeploymentTests.cs
│   │   ├── DebugSessionTests.cs
│   │   └── VsdbgTests.cs
│   │
│   ├── PiDbg.Transport.Tests/
│   │   ├── PiDbg.Transport.Tests.csproj
│   │   ├── SshConnectionManagerTests.cs
│   │   └── SftpTransferServiceTests.cs
│   │
│   ├── PiDbg.Deployment.Tests/
│   │   ├── PiDbg.Deployment.Tests.csproj
│   │   ├── DeploymentPackagerTests.cs
│   │   └── ManifestBuilderTests.cs
│   │
│   └── PiDbg.Agent.Tests/
│       ├── PiDbg.Agent.Tests.csproj
│       ├── VsdbgManagerTests.cs
│       └── ProcessLifecycleServiceTests.cs
│
├── proto/                             # Source-of-truth proto files (symlinked into PiDbg.Contracts)
│   ├── debug_agent.proto
│   ├── deployment.proto
│   ├── session.proto
│   └── common.proto
│
├── scripts/
│   ├── provision/
│   │   ├── install-agent.sh           # Downloads and installs PiDbg.Agent + systemd service
│   │   ├── install-vsdbg.sh           # Downloads vsdbg for ARM64
│   │   ├── setup-ssh-keys.sh          # Generates SSH keypair, installs pub key on Pi
│   │   └── uninstall.sh
│   ├── build/
│   │   ├── build-agent.ps1            # Builds ARM64 agent and packages for distribution
│   │   └── build-vsix.ps1
│   └── ci/
│       ├── run-integration-tests.sh
│       └── agent-matrix.sh            # Tests against multiple Pi OS images
│
└── docs/
    ├── 01-architecture-overview.md
    ├── 02-repository-layout.md
    ├── 03-project-list.md
    ├── 04-interfaces.md
    ├── 05-services.md
    ├── 06-threading-model.md
    ├── 07-transport-design.md
    ├── 08-lifecycle-diagrams.md
    ├── 09-deployment-flow.md
    ├── 10-debugger-attach-flow.md
    ├── 11-error-handling.md
    ├── 12-logging-strategy.md
    ├── 13-security-model.md
    ├── 14-update-strategy.md
    └── 15-roadmap.md
```

---

## Naming Conventions

| Scope | Convention | Example |
|-------|-----------|---------|
| Solution | PiDbg.* | PiDbg.Agent |
| Interfaces | IXxx | IVsdbgManager |
| Service implementations | XxxService | ProcessLifecycleService |
| gRPC service impl | XxxGrpcService | AgentGrpcService |
| Records / immutable DTOs | XxxRecord / XxxInfo | DeviceRecord |
| Proto message types | XxxRequest / XxxResponse | StartSessionRequest |
| Test classes | XxxTests | VsdbgManagerTests |
| VS UI classes | XxxWindow / XxxControl | DeviceManagerWindow |

---

## Project References

```
PiDbg.Vsix
  → PiDbg.Contracts        (proto types, gRPC client stubs)
  → PiDbg.Transport        (SSH.NET, SFTP)
  → PiDbg.Deployment       (packager, transfer)
  → PiDbg.DeviceManagement (registry, discovery)
  → PiDbg.Shared           (constants)

PiDbg.Agent
  → PiDbg.Contracts        (proto types, gRPC server stubs)
  → PiDbg.Shared           (constants)

PiDbg.Deployment
  → PiDbg.Transport        (SFTP transfer)
  → PiDbg.Contracts        (manifest DTOs)
  → PiDbg.Shared

PiDbg.DeviceManagement
  → PiDbg.Transport        (SSH connection)
  → PiDbg.Shared

PiDbg.Transport
  → PiDbg.Shared

PiDbg.Contracts
  → (no internal dependencies)

PiDbg.Shared
  → (no internal dependencies)
```

The dependency graph is a DAG — there are no cycles.

---

## Pi-side Directory Layout

All agent files on the Pi follow this layout:

```
/opt/pidbg/
├── agent/
│   ├── pidbg-agent                # Self-contained binary
│   └── appsettings.json           # Agent config
├── apps/
│   └── <deployment-id>/
│       ├── current/               # Active deployment (rename target)
│       ├── staging/               # In-progress upload (deleted on failure)
│       └── previous/              # Last known-good (rollback target)
├── vsdbg/
│   ├── vsdbg                      # vsdbg binary
│   └── vsdbg-ui                   # vsdbg helper
└── logs/
    └── pidbg-agent.log            # Rotated, max 10 MB × 5 files

/etc/pidbg/
└── agent.conf                     # System-wide config (ports, paths)

~/.config/systemd/user/
└── pidbg-agent.service            # systemd user service unit
```
