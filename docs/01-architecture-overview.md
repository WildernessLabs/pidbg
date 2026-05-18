# PiDbg — Architecture Overview

## 1. Purpose

PiDbg enables .NET 10 C# developers to debug applications on Raspberry Pi ARM64 devices
(Raspberry Pi OS 64-bit, Debian 12) directly from Visual Studio 2026 or VS Code — with full
IDE fidelity: breakpoints, stepping, watch windows, locals, call stacks, and async debugging.

It is not a custom debugger. It is an orchestration and transport layer that arranges
Microsoft's vsdbg (the official .NET Core debugger) on the Pi and connects the IDE's
debugger engine to it over a secured SSH tunnel.

Both IDE integrations share the same orchestration core (`PiDbg.Core`) and differ only in
how the final debugger attach is handed off:

| IDE | Attach mechanism |
|-----|-----------------|
| Visual Studio 2026 | MIEngine via `IVsDebugger4.LaunchDebugTargets4()` |
| VS Code | `PiDbg.DebugAdapter` exe — DAP proxy between VS Code and vsdbg |

---

## 2. System Context Diagram

```
┌─────────────────────────────────────────────────────────────────────────┐
│  Developer Machine  (Windows 10/11)                                      │
│                                                                          │
│  ┌─────────────────────────────────┐  ┌──────────────────────────────┐  │
│  │  Visual Studio 2026             │  │  VS Code                     │  │
│  │                                 │  │                              │  │
│  │  ┌─────────────┐  ┌──────────┐  │  │  ┌────────────────────────┐  │  │
│  │  │ PiDbg VSIX  │  │VS Debug  │  │  │  │ PiDbg VS Code Ext.     │  │  │
│  │  │ (F5 hook,   │  │Engine    │  │  │  │ (launch.json handler,  │  │  │
│  │  │  profile UI)│  │MIEngine  │  │  │  │  status bar)           │  │  │
│  │  └──────┬──────┘  └────┬─────┘  │  │  └───────────┬────────────┘  │  │
│  │         │ PiDbg.Core   │MIEngine│  │              │ DAP (stdio)   │  │
│  │         │ (shared)     │TCP     │  │  ┌───────────▼────────────┐  │  │
│  └─────────┼──────────────┼────────┘  │  │ PiDbg.DebugAdapter.exe │  │  │
│            │              │           │  │ (DAP server + proxy)   │  │  │
│            │              │           │  │ uses PiDbg.Core        │  │  │
│            │              │           │  └───────────┬────────────┘  │  │
│            │              │           └──────────────┼───────────────┘  │
│            │              │                          │                   │
│  ┌─────────▼──────────────▼──────────────────────────▼─────────────┐   │
│  │  PiDbg.Core  (shared orchestration library)                      │   │
│  │  ┌──────────────────────────────────────────────────────────┐    │   │
│  │  │  SessionOrchestrator: SSH → Provision → Publish →        │    │   │
│  │  │    Deploy → StartDebugSession(gRPC) → OpenTunnel         │    │   │
│  │  │  Returns: DebugSessionInfo { LocalPort, Pid, DllPath }   │    │   │
│  │  └──────────────────────────────────────────────────────────┘    │   │
│  │                                                                   │   │
│  │  SSH.NET Transport Layer                                          │   │
│  │  ┌─────────────────────────────────────────────────────────┐     │   │
│  │  │  Single SSH session per device                          │     │   │
│  │  │  ForwardedPortLocal A → Pi:50051  (gRPC)               │     │   │
│  │  │  ForwardedPortLocal B → Pi:4024+  (vsdbg TCP)          │     │   │
│  │  │  SftpClient (same credentials)    (file deploy)        │     │   │
│  │  └─────────────────────────────────────────────────────────┘     │   │
│  └─────────────────────────────┬─────────────────────────────────────┘  │
└────────────────────────────────┼────────────────────────────────────────┘
                                 │ SSH port 22
                                 │
         ┌───────────────────────▼──────────────────────┐
         │  Raspberry Pi  (ARM64, Debian 12)             │
         │                                               │
         │  ┌───────────────────────────────────────┐    │
         │  │  pidbg-agent.service  (systemd user)  │    │
         │  │  ──────────────────────────────────   │    │
         │  │  gRPC server :50051 (127.0.0.1 only)  │    │
         │  │  DeploymentManager                    │    │
         │  │  ProcessLifecycleService              │    │
         │  │  VsdbgManager                         │    │
         │  └──────────────────┬────────────────────┘    │
         │                     │ spawn                    │
         │  ┌──────────────────▼────────────────────┐    │
         │  │  vsdbg                                │    │
         │  │  --server --port 4024                 │    │
         │  │  (127.0.0.1 only)                     │    │
         │  └──────────────────┬────────────────────┘    │
         │                     │ launch / attach          │
         │  ┌──────────────────▼────────────────────┐    │
         │  │  .NET 10 Application (debug target)   │    │
         │  └───────────────────────────────────────┘    │
         │                                               │
         │  ┌───────────────────────────────────────┐    │
         │  │  meadow-daemon.service  (co-existing) │    │
         │  │  REST :5000                           │    │
         │  │  MPAK OTA update management           │    │
         │  └───────────────────────────────────────┘    │
         └───────────────────────────────────────────────┘
```

---

## 3. Component Responsibilities

### 3.1  PiDbg.Vsix
The Visual Studio extension. Thin VS-specific shell.

Responsibilities:
- Register the "Raspberry Pi" debug profile type with VS
- Provide property pages for profile configuration (device, port, user, key, app path)
- Intercept F5 / start-without-debugging
- Call `SessionOrchestrator` (from `PiDbg.Core`) to drive steps 1–6
- Feed resulting `DebugSessionInfo` into `IVsDebugger4.LaunchDebugTargets4()` via MIEngine
- Expose a Device Manager tool window
- Stream agent logs to the VS Output window
- Terminate the debug session cleanly on stop

Does NOT:
- Implement any debug engine logic or DAP logic
- Know anything about the ICorDebug protocol
- Contain SSH, deployment, or provisioning logic (all in `PiDbg.Core`)

### 3.1b  PiDbg.VsCodeExtension
The VS Code extension. Thin TypeScript shell.

Responsibilities:
- Register the `pidbg` debug type in `package.json`
- Return a `DebugAdapterExecutable` descriptor pointing at `PiDbg.DebugAdapter`
- Provide `launch.json` schema contribution (host, port, user, key, appName)
- Status bar item showing connection state
- Device picker command

Does NOT:
- Implement any orchestration logic (all in `PiDbg.DebugAdapter` / `PiDbg.Core`)
- Manage SSH, deployment, or gRPC

### 3.1c  PiDbg.DebugAdapter
Standalone .NET executable. The DAP server for VS Code.

Responsibilities:
- Implement the Debug Adapter Protocol (DAP) server on stdin/stdout
- On `launch` request: drive steps 1–6 via `PiDbg.Core` (`SessionOrchestrator`)
- After session is started: proxy DAP messages between VS Code and vsdbg over the SSH tunnel
- On `disconnect`: tear down session and tunnel

Does NOT:
- Implement any debug engine internals
- Have any VS or VS Code SDK references
- Serve any network port (VS Code communicates via stdio)

### 3.2  PiDbg.Agent  (runs on Pi)
The on-device orchestrator. Lightweight, single-file, self-contained .NET 10 ARM64 binary.

Responsibilities:
- Serve the gRPC DebugAgent service on 127.0.0.1:50051
- Accept file deployment (chunked streaming upload, atomic swap)
- Manage vsdbg: verify installation, download if absent, launch with correct args
- Manage process lifecycle: start/stop/query the debug target
- Coordinate with Meadow.Daemon for graceful handoff of managed processes
- Stream structured log events back to VSIX

Does NOT:
- Implement any debug logic
- Serve any port accessible from outside localhost
- Manage SSH keys (it is SSH's client — SSH handles authentication)

### 3.3  PiDbg.Contracts
Protobuf definitions and generated gRPC code. Shared between VSIX and Agent.
Also contains immutable DTO records and shared constants.

### 3.4  PiDbg.Core
The shared orchestration library. Contains everything that both the VSIX and the
`PiDbg.DebugAdapter` need to drive a debug session:

- `SessionOrchestrator` — drives steps 1–6 (connect → provision → publish → deploy →
  start session → open tunnel), returns `DebugSessionInfo`
- Thin façades over `PiDbg.Transport`, `PiDbg.Deployment`, and the gRPC client

No VS SDK references. No TypeScript. Must be multi-targeted (net10.0 + net472) so the
VSIX (net472) and DebugAdapter (net10.0) can both consume it.

### 3.5  PiDbg.Transport
SSH.NET wrapper library. Manages the SSH session lifecycle, SFTP client, and port forwarding.
Used by `PiDbg.Core`. The agent never uses this library.

### 3.6  PiDbg.Deployment
Deployment packager and transfer logic. Used by `PiDbg.Core`.
Packages dotnet publish output, transfers via SFTP, instructs agent to swap.

### 3.7  PiDbg.DeviceManagement
Device registry (persistent JSON store), discovery (mDNS/Bonjour), and connection factory.
Used by `PiDbg.Core` and both IDE extensions.

---

## 4. Key Design Decisions

### Decision 1: gRPC, not REST, for VSIX↔Agent control plane
Meadow.Daemon uses REST. PiDbg.Agent uses gRPC because:
- Server-streaming RPCs are required for log tailing and session status events
- Protobuf eliminates JSON parsing error classes
- Deadlines and cancellation propagate through the stack automatically
- Client code is generated — no hand-rolled HTTP clients
- gRPC-over-SSH-tunnel is a single forwarded port, not a set of HTTP routes

### Decision 2: All remote TCP tunneled through SSH — zero open ports
The agent listens only on 127.0.0.1. vsdbg listens only on 127.0.0.1.
No application ports are exposed to the network. Authentication is entirely SSH.
This means:
- No firewall configuration needed on the Pi
- No application-layer authentication to design
- The only attack surface is the SSH daemon (standard, audited)

### Decision 3: Agent written in C#, not Rust like Meadow.Daemon
The existing Meadow.Daemon is Rust. PiDbg.Agent is C# because:
- The agent is a companion, not a fork or extension of Meadow.Daemon
- The target developers are .NET developers — the agent must be auditable by them
- .NET 10 self-contained single-file publish produces a viable ARM64 binary (~30 MB)
- gRPC library (Grpc.AspNetCore) is first-class .NET, not a Rust binding
- Sharing patterns with the VSIX (DI, cancellation, structured logging) reduces cognitive overhead

### Decision 4: vsdbg TCP server mode, not stdin/stdout pipe mode
vsdbg supports two remote modes:
- `--server --port N` (TCP)
- Pipe via stdin/stdout

TCP server mode is chosen because:
- The debugging protocol is cleanly separated from the transport (SSH)
- Easier diagnostics: port state is visible in `ss -tlnp`
- vsdbg's official documented remote debugging path for Visual Studio is TCP
- Multiple parallel debug sessions (different apps) can use different ports

### Decision 5: Atomic deployment via directory rename
Deploy sequence:
1. SFTP upload to `/opt/pidbg/apps/<id>/staging/`
2. Verify SHA-256 manifest
3. `rename("/opt/pidbg/apps/<id>/staging", "/opt/pidbg/apps/<id>/current")`
4. Previous `current` is kept as `previous` for one-step rollback
The rename is atomic on ext4. An interrupted deployment never corrupts the running app.

### Decision 6: Agent coordinates with Meadow.Daemon, does not replace it
When a debug session starts, the target app may already be running under Meadow.Daemon's
supervision. The agent calls Meadow.Daemon's REST API (`PUT /api/apply` with a stop action
or `DELETE /api/updates`) to request graceful shutdown before launching vsdbg.
After the debug session ends, Meadow.Daemon is notified to resume management.
This is a REST call to 127.0.0.1:5000 — no SSH required.

---

## 5. Data Flow Summary

### F5 Press → Breakpoint Hit (Visual Studio)

Steps 1–16 are driven by `PiDbg.Core.SessionOrchestrator`. Steps 17–20 are VS-specific.

```
1.  VS calls PiDbg VSIX DebugLaunchProvider.QueryDebugTargetsAsync()
2.  VSIX reads active Raspberry Pi launch profile
3.  SessionOrchestrator.RunAsync() begins:
4.    SSH connection from SshConnectionManager (connects or reuses)
5.    Opens gRPC tunnel: localhost:A → Pi:50051
6.    AgentClient.GetStatusAsync() — verifies agent alive
7.    AgentClient.GetVsdbgInfoAsync() — verifies vsdbg installed
8.    MSBuild runs dotnet publish (via IBuildManager, VS-specific; adapter uses CLI)
9.    DeploymentPackager bundles publish output + SHA-256 manifest
10.   SFTP streams deploy chunks to Pi staging directory
11.   AgentClient.CommitDeploymentAsync() — agent does atomic rename
12.   Allocates ephemeral local port B
13.   Opens vsdbg tunnel: localhost:B → Pi:4024
14.   AgentClient.StartDebugSessionAsync(port=4024, appPath, args)
15.   Agent calls Meadow.Daemon REST to stop managed process
16.   Agent spawns: vsdbg --server --port 4024 -- dotnet /opt/.../App.dll
17. SessionOrchestrator returns DebugSessionInfo { LocalPort=B, Pid, DllPath }
18. VSIX calls IVsDebugger4.LaunchDebugTargets4() with MIEngine + TCP localhost:B
19. VS debugger connects through tunnel to vsdbg
20. vsdbg attaches to .NET app
21. Developer hits breakpoint
```

### F5 Press → Breakpoint Hit (VS Code)

Steps 1–16 are identical (same `PiDbg.Core.SessionOrchestrator`). Step 17+ differs.

```
1.  VS Code reads launch.json (type: "pidbg")
2.  PiDbg VS Code extension returns DebugAdapterExecutable("pidbg-adapter.exe")
3.  VS Code spawns pidbg-adapter.exe, communicates via DAP on stdio
4.  Adapter receives DAP "launch" request
5.  Adapter calls SessionOrchestrator.RunAsync() — steps 4–16 above
6.  SessionOrchestrator returns DebugSessionInfo { LocalPort=B, Pid, DllPath }
7.  Adapter sends DAP "initialized" event to VS Code
8.  Adapter opens TCP connection to localhost:B (vsdbg)
9.  Adapter proxies DAP messages: VS Code stdio ↔ vsdbg TCP
10. vsdbg attaches to .NET app
11. Developer hits breakpoint
```

---

## 6. Port Allocation

| Port | Location | Listener | Accessible From |
|------|----------|----------|-----------------|
| 22   | Pi       | sshd     | Network (required) |
| 50051 | Pi      | PiDbg.Agent gRPC | 127.0.0.1 only |
| 4024–4124 | Pi | vsdbg (per session) | 127.0.0.1 only |
| 5000 | Pi       | Meadow.Daemon REST | 127.0.0.1 only |
| Dynamic | Dev machine | SSH.NET ForwardedPortLocal (gRPC) | localhost only |
| Dynamic | Dev machine | SSH.NET ForwardedPortLocal (vsdbg) | localhost only |

---

## 7. Non-Goals

- No custom debug engine
- No CLR debugging internals
- No replacement of vsdbg
- No support for non-ARM64 targets (initially)
- No support for non-Debian Linux (initially)
- No support for .NET Framework or Mono
- No Windows SSH server support
- No GUI installer for developer machine (VSIX / VS Code extension handles it)
- No Neovim / other editor DAP clients (architecture permits it, but not V1)
