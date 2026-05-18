# PiDbg — VS Code Extension Architecture

## 1. Overview

The VS Code integration consists of three layers:

| Layer | Technology | Responsibility |
|-------|-----------|----------------|
| `PiDbg.VsCodeExtension` | TypeScript | VS Code API integration; launch.json schema; status bar |
| `PiDbg.DebugAdapter` | C# / .NET 10 | DAP server; drives session orchestration; proxies DAP to vsdbg |
| `PiDbg.Core` | C# / .NET 10+472 | Session orchestration (shared with VSIX) |

VS Code communicates with `pidbg-adapter.exe` via the Debug Adapter Protocol over stdio.
The adapter handles all SSH, gRPC, deployment, and vsdbg concerns. The TypeScript extension
is intentionally minimal.

---

## 2. Component Diagram

```
VS Code
│
│  launch.json: { "type": "pidbg", "host": "...", ... }
│
├─ PiDbg.VsCodeExtension (TypeScript)
│    activate()
│      └─ registers PiDbgDebugAdapterDescriptorFactory
│           createDebugAdapterDescriptor()
│             └─ returns DebugAdapterExecutable("pidbg-adapter.exe", [...])
│
│  DAP messages over stdio
│
└─ pidbg-adapter.exe  (PiDbg.DebugAdapter, .NET 10, win-x64)
     │
     │  initialize → respond with capabilities
     │  launch     → SessionOrchestrator.RunAsync()
     │                 ├─ SSH connect
     │                 ├─ Provision (idempotent)
     │                 ├─ dotnet publish (CliPublishRunner)
     │                 ├─ SFTP deploy
     │                 ├─ gRPC StartDebugSession → daemon
     │                 └─ SSH tunnel → localhost:B → Pi:4024
     │              → returns DebugSessionInfo { LocalPort=B, Pid, DllPath }
     │              → sends DAP "initialized" + "process" events
     │
     │  configurationDone → open TCP conn to localhost:B (vsdbg)
     │                    → start DapProxy loop
     │
     │  [proxy loop: VS Code stdio ↔ vsdbg TCP]
     │
     │  disconnect → SessionOrchestrator.StopAsync()
     │             → close tunnel
     │
     └─ vsdbg (on Pi, tunneled to localhost:B)
```

---

## 3. DAP Adapter Design

### 3.1 Startup

`pidbg-adapter.exe` reads DAP messages from **stdin** and writes responses to **stdout**.
Structured logs go to **stderr** (VS Code surfaces these in the Debug Console).

It uses `Microsoft.VisualStudio.Shared.VsCodeDebugProtocol` (or a compatible DAP library)
for message framing and dispatch.

### 3.2 `initialize` Request

Respond immediately with `InitializeResponse` advertising capabilities:

```json
{
  "supportsConfigurationDoneRequest": true,
  "supportsTerminateRequest": true,
  "supportTerminateDebuggee": true
}
```

Do **not** start orchestration here — wait for `launch`.

### 3.3 `launch` Request

Arguments map to `SessionRequest`:

| launch.json key | SessionRequest field |
|----------------|---------------------|
| `host` | `Connection.Host` |
| `port` | `Connection.Port` (default 22) |
| `username` | `Connection.Username` |
| `privateKeyPath` | `Connection.PrivateKeyPath` |
| `password` | `Connection.Password` (fallback) |
| `appName` | `AppName` |
| `rootFolder` | `RootFolder` (default `~/meadow`) |
| `projectPath` | `ProjectPath` (path to .csproj) |
| `args` | `AppArgs` |

Flow:
1. Call `SessionOrchestrator.RunAsync(request, progressReporter, ct)`
2. Progress events → DAP `output` events (shown in Debug Console)
3. On success → send `initialized` event, then `process` event with PID
4. On failure → send `output` (error) + `terminated` event; exit with non-zero code

### 3.4 `configurationDone` Request

VS Code sends this after processing breakpoints. At this point:
1. Open TCP connection to `localhost:{DebugSessionInfo.LocalPort}` (vsdbg via SSH tunnel)
2. Start `DapProxy` — bidirectional copy: stdin→TCP (requests) and TCP→stdout (events/responses)
3. The adapter becomes transparent; all further DAP traffic is between VS Code and vsdbg

### 3.5 `disconnect` / `terminate` Request

1. Stop `DapProxy` (close TCP connection)
2. Call `SessionOrchestrator.StopAsync(sessionHandle, ct)`
   - Sends `StopDebugSession` gRPC call to agent
   - Agent terminates vsdbg + app process
3. Close SSH tunnel
4. Send `terminated` event; exit 0

---

## 4. `DapProxy` Design

The proxy is a tight bidirectional copy with DAP message framing awareness:

```
stdin (VS Code → adapter) ──► read DAP frame ──► write to TCP socket (vsdbg)
TCP socket (vsdbg → adapter) ──► read DAP frame ──► write to stdout (VS Code)
```

DAP message framing: `Content-Length: N\r\n\r\n{json}` — the proxy must buffer and
re-frame correctly; it cannot do raw byte copy because the adapter needs to intercept
`disconnect` before forwarding (to run teardown).

Intercept these before forwarding:
- `disconnect` / `terminate` — run teardown, then forward to vsdbg
- Any message that should not reach vsdbg (none currently)

All other messages pass through unchanged.

---

## 5. `IPublishRunner` Abstraction

The VSIX triggers `dotnet publish` via VS's `IBuildManager` (which respects the active
solution configuration, platform, etc.). The DebugAdapter has no access to VS APIs, so it
uses `CliPublishRunner` instead.

```csharp
public interface IPublishRunner
{
    Task<PublishResult> PublishAsync(
        string projectPath,
        string appName,
        IProgress<string> progress,
        CancellationToken ct);
}
```

`CliPublishRunner` invokes:
```
dotnet publish <projectPath> -c Release -r linux-arm64 --self-contained false -o <outDir>
```

`MsBuildPublishRunner` (VSIX only) delegates to `IBuildManager.BuildAsync()` with the
active project configuration.

`SessionOrchestrator` accepts `IPublishRunner` via constructor injection. The VSIX
provides `MsBuildPublishRunner`; the adapter provides `CliPublishRunner`.

---

## 6. VS Code Extension Structure

### `package.json` contribution points

```json
{
  "contributes": {
    "debuggers": [{
      "type": "pidbg",
      "label": "Raspberry Pi (.NET)",
      "languages": ["csharp"],
      "configurationAttributes": {
        "launch": {
          "required": ["host", "username", "appName"],
          "properties": {
            "host":           { "type": "string", "description": "Pi hostname or IP" },
            "port":           { "type": "number", "default": 22 },
            "username":       { "type": "string" },
            "privateKeyPath": { "type": "string", "description": "Path to SSH private key" },
            "password":       { "type": "string", "description": "SSH password (prefer key auth)" },
            "appName":        { "type": "string" },
            "rootFolder":     { "type": "string", "default": "~/meadow" },
            "projectPath":    { "type": "string", "description": "Path to .csproj (defaults to workspace)" },
            "args":           { "type": "array", "items": { "type": "string" } }
          }
        }
      },
      "initialConfigurations": [{
        "type": "pidbg",
        "request": "launch",
        "name": "Debug on Raspberry Pi",
        "host": "raspberrypi.local",
        "username": "pi",
        "privateKeyPath": "${userHome}/.ssh/pidbg_rsa",
        "appName": "${workspaceFolderBasename}"
      }]
    }],
    "breakpoints": [{ "language": "csharp" }]
  }
}
```

### Build pipeline

```
build.ps1
  1. dotnet publish src/PiDbg.DebugAdapter -r win-x64 --self-contained -o src/PiDbg.VsCodeExtension/bin/
  2. cd src/PiDbg.VsCodeExtension && npm install && npm run compile
  3. vsce package --out ../../dist/pidbg-vscode-{version}.vsix
```

The adapter binary is committed to `bin/.gitkeep`; the actual exe is copied at build time
and excluded from source control via `.vscodeignore`.

---

## 7. Failure Modes

| Failure | Adapter behavior |
|---------|-----------------|
| SSH auth failure | `launch` returns error message; sends `terminated`; exit 1 |
| Provision failure | `output` event with error detail; `terminated`; exit 1 |
| `dotnet publish` failure | `output` with build errors; `terminated`; exit 1 |
| Deployment failure | `output` with error; `terminated`; exit 1 |
| Agent not running | `output` with "agent not found — run provision"; `terminated`; exit 1 |
| vsdbg connection timeout | `output` with timeout message; `terminated`; exit 1 |
| SSH tunnel drops mid-session | `DapProxy` gets read error → sends `terminated` event |
| `disconnect` during orchestration | `CancellationToken` cancelled; teardown runs; exit 0 |

All errors include enough context to be actionable (what failed, what to check).
SSH and gRPC errors include the remote address and operation name.

---

## 8. Security Considerations

- The adapter inherits the SSH key / password from `launch.json`. VS Code stores
  `launch.json` in `.vscode/` — secrets should use `privateKeyPath`, not `password`.
- The adapter does **not** log connection credentials; it logs the host and username only.
- `pidbg-adapter.exe` is signed (Authenticode) as part of the VS Code extension packaging.
- The extension should advise against committing `launch.json` with `password` set.

---

## 9. Testing Strategy

| Test type | What it covers |
|-----------|---------------|
| `PiDbg.Core` unit tests | `SessionOrchestrator` with mock transport/deployment/agent |
| `PiDbg.DebugAdapter` unit tests | DAP message handling; `DapProxy` with mock TCP |
| VS Code extension integration | `vscode-test` runner; adapter launched against mock Pi |
| End-to-end | Real Pi; VS Code F5 → breakpoint hit |
