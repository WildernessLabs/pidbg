# PiDbg — Architecture Overview

## 1. Purpose

PiDbg enables .NET 10 C# developers to debug applications on Raspberry Pi ARM64 devices
(Raspberry Pi OS 64-bit, Debian 12) directly from Visual Studio 2026 — with full IDE
fidelity: breakpoints, stepping, watch windows, locals, call stacks, and async debugging.

It is not a custom debugger. It is an orchestration and transport layer that arranges
Microsoft's vsdbg (the official .NET Core debugger) on the Pi and connects Visual Studio's
existing debugger engine to it over a secured SSH tunnel.

---

## 2. System Context Diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│  Developer Machine  (Windows 10/11)                                       │
│                                                                           │
│  ┌──────────────────────────────────────────────────────────────────┐    │
│  │  Visual Studio 2026                                              │    │
│  │                                                                  │    │
│  │  ┌────────────────────┐   ┌────────────────┐   ┌─────────────┐  │    │
│  │  │  PiDbg VSIX        │   │  VS Debugger   │   │ Output Win  │  │    │
│  │  │  ─────────────     │   │  Engine        │   │ (PiDbg)     │  │    │
│  │  │  Debug profile     │   │  (.NET Core)   │   └─────────────┘  │    │
│  │  │  Device manager    │   └────────┬───────┘                    │    │
│  │  │  Deploy orchestr.  │            │ MIEngine/ICorDebug          │    │
│  │  │  gRPC client       │            │ TCP → SSH tunnel            │    │
│  │  └──────────┬─────────┘            │                            │    │
│  │             │ gRPC (SSH tunnel)    │                            │    │
│  └─────────────┼──────────────────────┼────────────────────────────┘    │
│                │                      │                                   │
│  ┌─────────────▼──────────────────────▼────────────────────────────┐    │
│  │  SSH.NET Transport Layer                                         │    │
│  │  ┌─────────────────────────────────────────────────────────┐    │    │
│  │  │  Single SSH session per device                          │    │    │
│  │  │  ForwardedPortLocal A → Pi:50051  (gRPC)               │    │    │
│  │  │  ForwardedPortLocal B → Pi:4024+  (vsdbg TCP)          │    │    │
│  │  │  SftpClient (same credentials)    (file deploy)        │    │    │
│  │  └─────────────────────────────────────────────────────────┘    │    │
│  └──────────────────────────┬───────────────────────────────────────┘    │
└─────────────────────────────┼──────────────────────────────────────────┘
                              │ SSH port 22
                              │
        ┌─────────────────────▼────────────────────────┐
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
The Visual Studio extension. Owns the entire developer-machine side.

Responsibilities:
- Register the "Raspberry Pi" debug profile type with VS
- Provide property pages for profile configuration (device, port, user, key, app path)
- Intercept F5 / start-without-debugging
- Drive the full deploy → launch → attach sequence
- Expose a Device Manager tool window
- Stream agent logs to the VS Output window
- Terminate the debug session cleanly on stop

Does NOT:
- Implement any debug engine logic
- Know anything about the ICorDebug protocol
- Manage SSH keys (delegates to PiDbg.Transport)

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

### 3.4  PiDbg.Transport
SSH.NET wrapper library. Manages the SSH session lifecycle, SFTP client, and port forwarding.
Used exclusively by VSIX. The agent never uses this library.

### 3.5  PiDbg.Deployment
Deployment packager and transfer logic. Used by VSIX.
Packages dotnet publish output, transfers via SFTP, instructs agent to swap.

### 3.6  PiDbg.DeviceManagement
Device registry (persistent JSON store), discovery (mDNS/Bonjour), and connection factory.
Used by VSIX.

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

### F5 Press → Breakpoint Hit

```
1. VS calls PiDbg VSIX DebugLaunchProvider.QueryDebugTargetsAsync()
2. VSIX reads active Raspberry Pi launch profile
3. VSIX requests SSH connection from SshConnectionManager (connects or reuses)
4. VSIX opens gRPC tunnel: localhost:A → Pi:50051
5. VSIX calls AgentClient.GetStatusAsync() — verifies agent alive
6. VSIX calls AgentClient.GetVsdbgInfoAsync() — verifies vsdbg installed
7. MSBuild runs dotnet publish (self-triggered by VSIX via IBuildManager)
8. DeploymentPackager bundles publish output + SHA-256 manifest
9. VSIX streams deploy chunks over SFTP to Pi staging directory
10. VSIX calls AgentClient.CommitDeploymentAsync() — agent does atomic rename
11. VSIX allocates ephemeral local port B
12. VSIX opens vsdbg tunnel: localhost:B → Pi:4024
13. VSIX calls AgentClient.StartDebugSessionAsync(port=4024, appPath, args)
14. Agent calls Meadow.Daemon REST to stop managed process
15. Agent spawns: vsdbg --server --port 4024 -- dotnet /opt/pidbg/apps/X/current/App.dll
16. VSIX receives session-started event with PID
17. VSIX calls IVsDebugger4.LaunchDebugTargets4() with:
      Engine: {2E36F1D4-B23C-435D-AB41-18E608940038} (Managed .NET Core)
      Transport: TCP
      Address: localhost:B
18. VS debugger connects through tunnel to vsdbg
19. vsdbg launches .NET app under debug
20. Developer hits breakpoint
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
- No GUI installer for developer machine (VSIX handles it)
