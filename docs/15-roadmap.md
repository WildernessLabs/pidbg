# PiDbg — Phased Implementation Roadmap

---

## Phase 0: Foundation & Scaffolding
**Duration:** 1–2 weeks  
**Goal:** Solution structure, toolchain, CI, basic SSH connectivity

### Deliverables
- [ ] Create `.slnx` solution with all projects
- [ ] Configure multi-targeting (net10.0 + net472) for library projects
- [ ] Set up VSIX project with minimal `AsyncPackage` that loads without error
- [ ] Set up `PiDbg.Agent` with Kestrel + minimal gRPC server
- [ ] Define all `.proto` files and run `Grpc.Tools` codegen
- [ ] Define all interfaces (`IDeviceRegistry`, `ISshConnectionManager`, etc.)
- [ ] Set up GitHub Actions CI:
  - Build all projects
  - Run unit tests
  - Build ARM64 agent (publish)
- [ ] Provision scripts: `setup-pi.sh`, `install-agent.sh`
- [ ] Logging infrastructure: Serilog in both VSIX and Agent
- [ ] `DeviceRegistry` with JSON persistence

### Exit criteria
- `pidbg-agent` starts on Pi, logs "PiDbg.Agent ready", accepts gRPC Ping
- VSIX installs into VS without errors
- `SshConnectionManager.ConnectAsync()` works in a unit test against a mock SSH server

---

## Phase 1: Core SSH + Deployment
**Duration:** 3–4 weeks  
**Goal:** Working file deployment to Pi from VSIX

### Deliverables
- [ ] `SshConnectionManager` — full connect/disconnect/reconnect
- [ ] `SftpTransferService` — upload files with progress
- [ ] `DeploymentPackager` — reads publish output, builds SHA-256 manifest
- [ ] `DeploymentService` — full deploy sequence (package → SFTP → commit)
- [ ] `DeploymentManager` on Agent — staging, atomic rename, verification
- [ ] VSIX: "Add Device" dialog (host, port, username, key path)
- [ ] VSIX: Device Manager tool window (list devices, connect/disconnect, deploy test)
- [ ] VSIX: MSBuild integration (trigger dotnet publish via `IBuildManager`)
- [ ] VSIX: Progress reporting in Output window
- [ ] VSIX: gRPC `AgentClient` wrapper with all error mapping

### Exit criteria
- User adds a Pi in Device Manager
- Clicks "Deploy" on a project
- Files appear in `/opt/pidbg/apps/MyApp/current/` on Pi
- SHA-256 manifest verified by agent
- Rollback to previous version works

---

## Phase 2: vsdbg + Debugger Attach
**Duration:** 3–4 weeks  
**Goal:** F5 launches app on Pi and VS debugger attaches

### Deliverables
- [ ] `VsdbgManager` — install, version check
- [ ] `VsdbgLauncher` — spawn vsdbg in TCP server mode
- [ ] Agent: `StartSession` / `StopSession` RPCs implemented
- [ ] Agent: Meadow.Daemon coordination (`MeadowDaemonClient`)
- [ ] VSIX: SSH port forward for vsdbg tunnel
- [ ] VSIX: `DebugSessionOrchestrator` — full F5 sequence
- [ ] VSIX: `RaspberryPiDebugLaunchProvider` — intercepts F5
- [ ] VSIX: `RaspberryPiDebugProfileProvider` — "Raspberry Pi" in dropdown
- [ ] VSIX: Property pages for launch profile (device, remote path, args)
- [ ] VSIX: `IVsDebugger4.LaunchDebugTargets4()` attach call
- [ ] VSIX: Session teardown on Stop
- [ ] Integration tests: F5 → breakpoint hit (against real Pi)

### Exit criteria
- Press F5 on a C# .NET 10 project with "Raspberry Pi" profile selected
- App is built, deployed, and started on Pi
- VS debugger attaches
- Breakpoints hit, stepping works, locals visible, call stack populated
- Press Stop: session terminates cleanly

---

## Phase 3: Debug Feature Completeness
**Duration:** 2–3 weeks  
**Goal:** Full VS debugger feature parity

### Deliverables
- [ ] Watch window expressions work
- [ ] Conditional breakpoints work
- [ ] Exception settings (break on thrown vs. user-unhandled) work
- [ ] Async debugging: Tasks window shows pending async operations
- [ ] `async/await` stack frames correctly attributed
- [ ] Edit environment variables in launch profile
- [ ] Multi-project support (solution with multiple deployable projects)
- [ ] Log streaming from agent to Output window (real-time)

### Exit criteria
- All items in the "Debugging Features Enabled" table in doc 10 verified
- Async `await` stack traces are correct in the Call Stack window

---

## Phase 4: Developer Experience Polish
**Duration:** 2–3 weeks  
**Goal:** Production-quality UX

### Deliverables
- [ ] Device Manager: live device status (connected/disconnected/version)
- [ ] Device Manager: "Collect Diagnostic Info" command
- [ ] Device Manager: "Show vsdbg log" command
- [ ] Delta deployment (only transfer changed files)
- [ ] Connection status in VS status bar
- [ ] "Reconnect" button in Device Manager on disconnect
- [ ] Informative Output window messages at every step
- [ ] Error guidance messages (e.g., "Run: sudo systemctl enable pidbg-agent")
- [ ] Progress dialog for initial vsdbg install (with cancel)
- [ ] Launch profile UI: "Test Connection" button

### Exit criteria
- First-time setup experience (fresh Pi): user follows Output window to go from zero
  to first breakpoint hit without needing documentation
- Error messages include actionable guidance for all known failure modes

---

## Phase 5: Agent Self-Update + vsdbg Auto-Update
**Duration:** 1–2 weeks  
**Goal:** Friction-free version management

### Deliverables
- [ ] VSIX bundles agent binary (ARM64 self-contained)
- [ ] `PrepareUpdate` + `ApplyUpdate` RPCs on agent
- [ ] VSIX: version mismatch detection + silent auto-update (stable channel)
- [ ] VSIX: update progress in Output window
- [ ] Rollback on failed agent update
- [ ] vsdbg version check on every F5 + auto-update
- [ ] Air-gapped vsdbg install (VSIX bundles vsdbg tarball)
- [ ] Protocol version negotiation (hard block on incompatible protocol)

### Exit criteria
- Developer installs VSIX update → next F5 automatically updates agent on Pi
- Agent update completes in < 30 seconds without user intervention
- If update fails, previous version is restored automatically

---

## Phase 6: Device Discovery + Multi-Device
**Duration:** 2–3 weeks  
**Goal:** Discover Pis automatically; support teams with multiple devices

### Deliverables
- [ ] mDNS service advertisement in agent (`_pidbg._tcp.local`)
- [ ] mDNS discovery in Device Manager (auto-populate discovered devices list)
- [ ] Multiple devices in launch profile (select target device from dropdown)
- [ ] Device labels/tags in registry
- [ ] "Attach to Running Process" in Device Manager (Phase 2 attach-mode debug)
- [ ] Agent supports multiple simultaneous debug sessions (different ports)
- [ ] VSIX: "Deploy to all tagged devices" (CI/CD integration scenario)

### Exit criteria
- On a network with a Pi running the agent, Device Manager shows it without manual entry
- Two developers can debug different apps on the same Pi simultaneously
- Attach to running process works

---

## Phase 7: Production Hardening
**Duration:** 2–3 weeks  
**Goal:** Reliability, edge cases, performance

### Deliverables
- [ ] Integration test suite covering all failure modes (SSH drop, disk full, etc.)
- [ ] Load test: deploy + attach 10 times in a row without state corruption
- [ ] Parallel unit test: agent handles concurrent gRPC requests correctly
- [ ] Memory leak audit (VS extensions are long-lived processes)
- [ ] Agent startup time < 1 second verified on Pi Zero 2W (slowest target)
- [ ] Agent idle memory < 30 MB RSS verified
- [ ] Full VSIX uninstall leaves no registry debris or orphaned processes
- [ ] SSH.NET upgrade to latest stable
- [ ] Security review: key storage, port exposure, update authenticity

### Exit criteria
- All known failure modes have a test case
- Agent runs stable for 8+ hours under continuous debug-session cycling
- No memory growth in VS process after 20 debug sessions

---

## Phase 8: PiDbg.Core Extraction + VS Code Support
**Duration:** 3–4 weeks
**Goal:** Extract shared orchestration into `PiDbg.Core`; ship a working VS Code extension

### Context
Steps 1–6 of the debug session flow (SSH → provision → publish → deploy → start session →
open tunnel) currently live inside `PiDbg.Vsix`. Extracting them into `PiDbg.Core` makes
them reusable. `PiDbg.DebugAdapter` consumes `PiDbg.Core` and implements a DAP server that
VS Code launches as a subprocess. The VS Code extension itself is a thin TypeScript shell.

### Deliverables

**PiDbg.Core (new library):**
- [ ] `SessionOrchestrator` — async, cancellable; accepts `SessionRequest`, returns `DebugSessionInfo`
- [ ] `IPublishRunner` + `CliPublishRunner` (wraps `dotnet publish` CLI)
- [ ] `MsBuildPublishRunner` (wraps VS `IBuildManager`) — moved from VSIX
- [ ] Refactor VSIX: `RaspberryPiDebugLaunchProvider` delegates to `SessionOrchestrator`
- [ ] Confirm VSIX behavior unchanged end-to-end after refactor

**PiDbg.DebugAdapter (new executable):**
- [ ] DAP server on stdin/stdout (`initialize`, `launch`, `configurationDone`, `disconnect`)
- [ ] `launch` handler calls `SessionOrchestrator.RunAsync()`
- [ ] `DapProxy` — TCP↔stdio bridge to vsdbg after session is established
- [ ] Structured logging to stderr (VS Code surfaces it in the debug console)
- [ ] win-x64 self-contained publish; bundled into VS Code extension `bin/`

**PiDbg.VsCodeExtension (new TypeScript project):**
- [ ] `package.json` — `debuggers` contribution, `pidbg` type, launch.json schema
- [ ] `debugAdapterFactory.ts` — returns `DebugAdapterExecutable` for `pidbg-adapter.exe`
- [ ] `devicePicker.ts` — "PiDbg: Connect to Device" command (reads `devices.json`)
- [ ] `statusBar.ts` — connection indicator
- [ ] `schemas/pidbg-launch.schema.json` — full IntelliSense for launch.json
- [ ] `scripts/build.ps1` — compiles adapter, copies exe, runs `vsce package`
- [ ] Integration test: VS Code F5 → breakpoint hit on real Pi

### Exit criteria
- VSIX behavior is unchanged after Core extraction (regression tests pass)
- VS Code F5 on a C# .NET 10 project with `"type": "pidbg"` profile:
  - Builds, deploys, attaches
  - Breakpoints hit, stepping works
- `pidbg-adapter.exe` starts in < 500 ms
- VS Code extension packages cleanly with `vsce package`

---

## Phase 9: Ecosystem Extensions (Backlog)
Not scheduled — collected for future planning:

- Hot Reload support (requires `MetadataUpdateHandlerAttribute` + vsdbg protocol extensions)
- Profiling integration (requires separate profiler attach, not vsdbg)
- ARM32 (Raspberry Pi OS 32-bit) support
- Alpine Linux support
- Automatic Pi provisioning wizard (SSH in, run install-agent.sh automatically)
- Remote file system browser in Device Manager
- Environment variable management per-device in UI
- Other DAP-capable editors (Neovim, Helix) via `pidbg-adapter.exe` — architecture already supports it

---

## Timeline Summary

| Phase | Duration | Cumulative |
|-------|----------|-----------|
| Phase 0: Scaffolding | 1–2 weeks | 2 weeks |
| Phase 1: Deployment | 3–4 weeks | 6 weeks |
| Phase 2: Debug Attach | 3–4 weeks | 10 weeks |
| Phase 3: Feature Completeness | 2–3 weeks | 13 weeks |
| Phase 4: UX Polish | 2–3 weeks | 16 weeks |
| Phase 5: Self-Update | 1–2 weeks | 18 weeks |
| Phase 6: Discovery | 2–3 weeks | 21 weeks |
| Phase 7: Hardening | 2–3 weeks | 24 weeks |
| Phase 8: Core + VS Code | 3–4 weeks | 28 weeks |

**Phase 1 completion = first usable deploy without debugging (~6 weeks)**  
**Phase 2 completion = first working remote debug session (~10 weeks)**  
**Phase 4 completion = beta-quality VS product (~16 weeks)**  
**Phase 8 completion = VS Code parity (~28 weeks)**

---

## Critical Path

The critical dependency chain is linear through Phase 2, then branches:

```
SSH.NET transport
  → SFTP file transfer
    → DeploymentService
      → Agent DeploymentManager
        → VsdbgManager + VsdbgLauncher
          → SSH port forward for vsdbg
            → VS debugger attach API
              → ✓ Working VS debug session (Phase 2)
                → PiDbg.Core extraction (Phase 8)
                  → PiDbg.DebugAdapter
                    → PiDbg.VsCodeExtension
                      → ✓ Working VS Code debug session (Phase 8)
```

Phases 3–7 can be done in parallel by separate team members once Phase 2 is complete.
Phase 8 depends only on Phase 2 being complete — it can begin alongside Phases 3–5 if
staffing allows. The `PiDbg.Core` extraction is a refactor with no behavior change;
do it first, verify with the VSIX, then build the adapter and extension on top.
