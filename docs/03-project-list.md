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

### 4. PiDbg.Transport

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

### 5. PiDbg.Deployment

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

### 6. PiDbg.DeviceManagement

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

### 7. PiDbg.Shared

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

### 8. PiDbg.Integration.Tests

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

### 9. PiDbg.Transport.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |

**Purpose:** Unit tests for SSH connection manager, port forwarding, SFTP transfer.
Uses a mock SSH server (embedded Sshd or mock) to avoid real Pi dependency.

---

### 10. PiDbg.Deployment.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |

**Purpose:** Unit tests for deployment packager, manifest builder, delta calculator.
All tests run against local filesystem — no SSH required.

---

### 11. PiDbg.Agent.Tests

| Property | Value |
|----------|-------|
| Type | Test project (xUnit) |
| Target | net10.0 |
| Platform | Can run on Windows (linux-arm64 not required for unit tests) |

**Purpose:** Unit tests for VsdbgManager, ProcessLifecycleService, DeploymentManager.
All Linux-specific calls (process.Start, Directory.Move) are behind interfaces and mocked.

---

## Build Matrix Summary

| Project | net472 | net10.0 | linux-arm64 | Self-contained |
|---------|--------|---------|-------------|----------------|
| PiDbg.Vsix | ✓ | | | |
| PiDbg.Agent | | ✓ | ✓ | ✓ |
| PiDbg.Contracts | ✓ | ✓ | | |
| PiDbg.Transport | ✓ | ✓ | | |
| PiDbg.Deployment | ✓ | ✓ | | |
| PiDbg.DeviceManagement | ✓ | ✓ | | |
| PiDbg.Shared | ✓ | ✓ | | |
| *Tests | | ✓ | | |

The VSIX targets net472 because Visual Studio 2026 still hosts extensions in a net472
AppDomain. All library dependencies must multi-target to ensure compatibility.
