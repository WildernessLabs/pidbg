# PiDbg — Project List

## Project Inventory

### 1. PiDbg.Vsix

| Property | Value |
|----------|-------|
| Type | VSIX (Visual Studio Extension) |
| SDK | Microsoft.VisualStudio.Sdk |
| Target | net472 (VSIX requirement) |
| VS Minimum | 17.12 (Visual Studio 2026) |
| Output | PiDbg.vsix |
| Platforms | AnyCPU |

**Purpose:** The developer-facing Visual Studio extension. Provides the "Raspberry Pi" debug
profile, device management UI, and drives the entire deploy-debug lifecycle.

**Key NuGet dependencies:**
- `Microsoft.VisualStudio.Shell.Framework` (VS SDK)
- `Microsoft.VisualStudio.Shell.15.0`
- `Microsoft.VisualStudio.ProjectSystem.SDK` (CPS — for debug profile integration)
- `Microsoft.VisualStudio.Imaging`
- `Grpc.Net.Client` (gRPC client)
- `Google.Protobuf`
- `Microsoft.Extensions.DependencyInjection`
- `Microsoft.Extensions.Logging.Abstractions`
- `Serilog.Extensions.Logging`
- `Polly` (retry policies)
- `SSH.NET` (SSH/SFTP transport)

**Key VS SDK interfaces implemented:**
- `AsyncPackage` (extension entry point)
- `IDebugLaunchProvider` (intercept F5)
- `IDebugProfileProvider` / `ILaunchProfile` (custom debug profile type)
- `ToolWindowPane` (Device Manager window)
- `IProfileUIContext` (property pages)

---

### 2. PiDbg.Agent

| Property | Value |
|----------|-------|
| Type | Console application |
| SDK | Microsoft.NET.Sdk.Web (for Kestrel / gRPC hosting) |
| Target | net10.0 |
| Runtime | linux-arm64 |
| Publish | Self-contained, single-file, trimmed, AOT-compatible |
| Expected binary size | ~25–35 MB |

**Purpose:** The on-device daemon. Runs as a systemd user service on the Pi.
Serves the gRPC control plane, manages deployments, manages vsdbg lifecycle.

**Key NuGet dependencies:**
- `Grpc.AspNetCore` (gRPC server via Kestrel)
- `Google.Protobuf`
- `Microsoft.Extensions.Hosting`
- `Microsoft.Extensions.DependencyInjection`
- `Serilog.AspNetCore`
- `Serilog.Sinks.File`
- `Serilog.Sinks.Console`
- `Serilog.Formatting.Compact`
- `Polly.Extensions.Http`
- `System.Text.Json` (built-in)

**Constraints:**
- Must not reference SSH.NET (runs on Pi, is the SSH target)
- Must not reference any VS SDK packages
- Single-file publish — no side-car DLLs
- Must start in < 1 second
- Idle memory target: < 30 MB RSS

---

### 3. PiDbg.Contracts

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 (multi-target for VSIX) |
| Output | PiDbg.Contracts.dll |

**Purpose:** Single source of truth for the VSIX↔Agent protocol. Contains `.proto` files,
MSBuild Grpc.Tools invocation, generated C# types, and any hand-written extension methods
on the generated types.

**Key NuGet dependencies:**
- `Google.Protobuf`
- `Grpc.Tools` (build-time code generation)
- `Grpc.Net.Client` (for client stubs referenced by VSIX)
- `Grpc.Core.Api` (for server stubs referenced by Agent)

**Proto files compiled here:**
- `debug_agent.proto` — `DebugAgentService` definition
- `deployment.proto` — `DeploymentService`, chunk streaming types
- `session.proto` — `DebugSessionService`, session types
- `common.proto` — `Empty`, `AgentVersion`, `DeviceInfo` shared types

**Naming:** All generated types are in namespace `PiDbg.Contracts.V1`.
The `.proto` package is `pidbg.v1`.

---

### 4. PiDbg.Core

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 |
| Output | PiDbg.Core.dll |

**Purpose:** Shared orchestration library. Exposes `SessionOrchestrator`, which drives the
full connect → provision → publish → deploy → start-session → open-tunnel sequence and
returns a `DebugSessionInfo`. Both `PiDbg.Vsix` and `PiDbg.DebugAdapter` depend on this;
neither contains orchestration logic of its own.

**Key types:**
- `SessionOrchestrator` — primary entry point; accepts `SessionRequest`, returns `DebugSessionInfo`
- `DebugSessionInfo` — immutable record: `{ LocalPort, AppPid, AppDllPath, TunnelHandle }`
- `SessionRequest` — connection params + publish params + app name
- `IPublishRunner` — abstraction over `dotnet publish`; two implementations:
  - `MsBuildPublishRunner` (used by VSIX — invokes VS build manager)
  - `CliPublishRunner` (used by DebugAdapter — invokes `dotnet publish` CLI)

**Key NuGet dependencies:**
- `Microsoft.Extensions.Logging.Abstractions`
- `PiDbg.Contracts` (project reference)
- `PiDbg.Transport` (project reference)
- `PiDbg.Deployment` (project reference)
- `PiDbg.DeviceManagement` (project reference)

**Constraint:** No VS SDK references. No TypeScript. Must be callable from both net472 and net10.0.

---

### 4b. PiDbg.DebugAdapter

| Property | Value |
|----------|-------|
| Type | Console application |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0 |
| Runtime | win-x64 (shipped as self-contained alongside VS Code extension) |
| Output | pidbg-adapter.exe |

**Purpose:** Standalone DAP server for VS Code. VS Code launches this executable and
communicates with it via DAP on stdin/stdout. The adapter drives the full debug session
setup via `PiDbg.Core`, then proxies DAP messages between VS Code and vsdbg over the SSH
tunnel.

**DAP message handling:**
- `initialize` — respond with adapter capabilities
- `launch` — call `SessionOrchestrator.RunAsync()`; send `initialized` + `process` events
- `configurationDone` — open TCP connection to vsdbg on the SSH tunnel port; start proxying
- `disconnect` / `terminate` — call `SessionOrchestrator.StopAsync()`; tear down tunnel

**Key NuGet dependencies:**
- `Microsoft.VisualStudio.Shared.VsCodeDebugProtocol` or `Microsoft.Extensions.Logging.Abstractions`
- `PiDbg.Core` (project reference)

**Constraints:**
- No VS SDK references
- No UI or interactive stdin (VS Code owns the UX)
- Must start in < 500 ms (VS Code waits synchronously for `initialize` response)

---

### 4c. PiDbg.VsCodeExtension

| Property | Value |
|----------|-------|
| Type | VS Code extension (TypeScript) |
| Language | TypeScript 5.x |
| Engine | VS Code ^1.90.0 |
| Output | pidbg-vscode-x.y.z.vsix |
| Location | `src/PiDbg.VsCodeExtension/` |

**Purpose:** Thin TypeScript shell. Registers the `pidbg` debug type and returns a
`DebugAdapterExecutable` descriptor pointing at the bundled `pidbg-adapter.exe`. Contains
no orchestration logic.

**VS Code contribution points:**
- `debuggers` — type `pidbg`, label "Raspberry Pi (.NET)"
- `breakpoints` — applies to `csharp` language
- `commands` — "PiDbg: Connect to Device", "PiDbg: Show Output"
- `configuration` — launch.json schema (host, port, username, privateKeyPath, appName, rootFolder)
- `statusBarItem` — connection state indicator

**launch.json schema example:**
```json
{
  "type": "pidbg",
  "request": "launch",
  "name": "Debug on Pi",
  "host": "raspberrypi.local",
  "username": "pi",
  "privateKeyPath": "${userHome}/.ssh/pidbg_rsa",
  "appName": "${workspaceFolderBasename}",
  "rootFolder": "~/meadow"
}
```

**Key npm dependencies:**
- `@vscode/debugadapter` (DAP type definitions)
- `@vscode/debugprotocol`

**Constraint:** Does not bundle `ssh2` or any SSH library — all SSH/gRPC is handled by
`pidbg-adapter.exe`. The TypeScript code is intentionally minimal.

---

### 6. PiDbg.Transport

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 |
| Output | PiDbg.Transport.dll |

**Purpose:** Encapsulates all SSH.NET usage. Provides managed SSH session lifecycle,
port forwarding manager, SFTP transfer service, and device probing (validate OS, arch,
.NET version before first connect).

**Key NuGet dependencies:**
- `SSH.NET` (>= 2024.2.0)
- `Microsoft.Extensions.Logging.Abstractions`
- `Polly`
- `System.IO.Pipelines` (streaming SFTP transfers)

**Constraint:** No VS SDK references. Must be testable in isolation.

---

### 7. PiDbg.Deployment

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 |
| Output | PiDbg.Deployment.dll |

**Purpose:** Reads dotnet publish output, builds a deployment package with a SHA-256
manifest, transfers it via SFTP, and instructs the agent to commit the deployment.

**Key NuGet dependencies:**
- `Microsoft.Extensions.Logging.Abstractions`
- `PiDbg.Contracts` (project reference)
- `PiDbg.Transport` (project reference)

---

### 8. PiDbg.DeviceManagement

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 |
| Output | PiDbg.DeviceManagement.dll |

**Purpose:** Persistent device registry (JSON file, `%LOCALAPPDATA%\PiDbg\devices.json`),
device discovery (initially manual-add + mDNS in Phase 2), connection factory
(creates SSH + gRPC channel for a given device).

**Key NuGet dependencies:**
- `Microsoft.Extensions.Logging.Abstractions`
- `Zeroconf` (mDNS discovery, Phase 2)
- `System.Text.Json`
- `PiDbg.Transport` (project reference)

---

### 9. PiDbg.Shared

| Property | Value |
|----------|-------|
| Type | Class library |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0; net472 |
| Output | PiDbg.Shared.dll |

**Purpose:** Zero-dependency shared constants, path helpers, and retry policies.
Referenced by all other projects. Never references any other PiDbg project.

**Contents:**
- `Constants.cs` — port numbers, timeouts, version strings
- `PiPaths.cs` — remote path constants (`/opt/pidbg/...`)
- `WinPaths.cs` — local path constants (`%LOCALAPPDATA%\PiDbg\...`)
- `Retry/RetryPolicy.cs` — Polly policy factory (no Polly reference here — factory takes Polly types via generic constraints)

---

### 10. PiDbg.Integration.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| SDK | Microsoft.NET.Sdk |
| Target | net10.0 |

**Purpose:** End-to-end tests that connect to a real Raspberry Pi (or a mock SSH server).
Configured via environment variables: `PIDBG_TEST_HOST`, `PIDBG_TEST_USER`,
`PIDBG_TEST_KEY_PATH`. Tests are skipped if env vars are absent (CI-friendly).

**Key NuGet dependencies:**
- `xunit`, `xunit.runner.visualstudio`
- `FakeItEasy`
- `Microsoft.NET.Test.Sdk`
- Project references to all PiDbg source projects

---

### 11. PiDbg.Transport.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |

**Purpose:** Unit tests for SSH connection manager, port forwarding, SFTP transfer.
Uses a mock SSH server (embedded Sshd or mock) to avoid real Pi dependency.

---

### 12. PiDbg.Deployment.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |

**Purpose:** Unit tests for deployment packager, manifest builder, delta calculator.
All tests run against local filesystem — no SSH required.

---

### 13. PiDbg.Agent.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |
| Platform | Can run on Windows (linux-arm64 not required for unit tests) |

**Purpose:** Unit tests for VsdbgManager, ProcessLifecycleService, DeploymentManager.
All Linux-specific calls (process.Start, Directory.Move) are behind interfaces and mocked.

---

## Build Matrix Summary

| Project | net472 | net10.0 | win-x64 | linux-arm64 | Self-contained | TypeScript |
|---------|--------|---------|---------|-------------|----------------|------------|
| PiDbg.Vsix | ✓ | | | | | |
| PiDbg.Agent | | ✓ | | ✓ | ✓ | |
| PiDbg.Contracts | ✓ | ✓ | | | | |
| PiDbg.Core | ✓ | ✓ | | | | |
| PiDbg.DebugAdapter | | ✓ | ✓ | | ✓ | |
| PiDbg.VsCodeExtension | | | | | | ✓ |
| PiDbg.Transport | ✓ | ✓ | | | | |
| PiDbg.Deployment | ✓ | ✓ | | | | |
| PiDbg.DeviceManagement | ✓ | ✓ | | | | |
| PiDbg.Shared | ✓ | ✓ | | | | |
| *Tests | | ✓ | | | | |

Notes:
- VSIX targets net472 because Visual Studio 2026 hosts extensions in a net472 AppDomain. All library dependencies multi-target for compatibility.
- `PiDbg.DebugAdapter` is published as a win-x64 self-contained executable and bundled inside the VS Code extension's `bin/` folder.
- `PiDbg.VsCodeExtension` is built with `npm run compile` + `vsce package`; the adapter exe is copied into place before packaging.

## Project Dependency Graph

```
PiDbg.Vsix
  → PiDbg.Core
  → PiDbg.Contracts
  → PiDbg.DeviceManagement
  → PiDbg.Shared

PiDbg.DebugAdapter
  → PiDbg.Core
  → PiDbg.Shared

PiDbg.VsCodeExtension
  → (no .NET refs — launches PiDbg.DebugAdapter as a subprocess)

PiDbg.Core
  → PiDbg.Transport
  → PiDbg.Deployment
  → PiDbg.Contracts
  → PiDbg.DeviceManagement
  → PiDbg.Shared

PiDbg.Agent
  → PiDbg.Contracts
  → PiDbg.Shared

PiDbg.Deployment
  → PiDbg.Transport
  → PiDbg.Contracts
  → PiDbg.Shared

PiDbg.DeviceManagement
  → PiDbg.Transport
  → PiDbg.Shared

PiDbg.Transport
  → PiDbg.Shared

PiDbg.Contracts
  → (no internal dependencies)

PiDbg.Shared
  → (no internal dependencies)
```

The dependency graph remains a DAG — no cycles introduced by the new projects.
