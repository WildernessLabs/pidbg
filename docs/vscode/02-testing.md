# PiDbg VS Code Extension — Testing Guide

## 1. Extension Development Host (fastest feedback loop)

Open `Source/PiDbg.VsCodeExtension/` in VS Code, then press **F5**. This spawns a second VS
Code window ("Extension Development Host") with the extension loaded from source. Set
breakpoints in TypeScript, add a `launch.json` with `"type": "pidbg"`, and press F5 in
that second window to trigger a real debug session.

Requires the adapter exe to already be built and sitting in `bin/`:

```powershell
dotnet publish Source/PiDbg.DebugAdapter -r win-x64 --self-contained -o Source/PiDbg.VsCodeExtension/bin/
```

TypeScript changes take effect after `npm run compile` + reload the host window
(`Ctrl+Shift+P` → "Developer: Reload Window").

---

## 2. Test the Adapter Directly (no VS Code needed)

`pidbg-adapter.exe` speaks DAP over stdio. Drive it with any process that writes/reads DAP
frames. Quick smoke test via PowerShell:

```powershell
$msg   = '{"seq":1,"type":"request","command":"initialize","arguments":{"adapterID":"pidbg"}}'
$frame = "Content-Length: $($msg.Length)`r`n`r`n$msg"
echo $frame | .\bin\pidbg-adapter.exe
```

For structured assertions use `@vscode/debugadapter-testsupport`, which gives a typed DAP
client that connects to the adapter process:

```typescript
import { DebugClient } from '@vscode/debugadapter-testsupport';

const dc = new DebugClient('node', 'bin/pidbg-adapter.exe', 'pidbg');
await dc.start();
const response = await dc.initializeRequest();
// assert response.body.supportsConfigurationDoneRequest === true
await dc.stop();
```

This layer is the right place for unit and integration coverage of DAP protocol handling
and `SessionOrchestrator` behavior, independent of VS Code.

---

## 3. Automated Extension Tests with `@vscode/test-cli`

The official framework launches a real VS Code process headlessly and runs a test suite
inside it.

Setup:

```jsonc
// .vscode-test.mjs
import { defineConfig } from '@vscode/test-cli';
export default defineConfig({ files: 'out/test/**/*.test.js' });
```

Run:

```
npm run compile && npx @vscode/test-cli --run .vscode-test.mjs
```

Use for: verifying the extension activates, registers the `pidbg` debug type, and returns
the correct `DebugAdapterDescriptor`. Not suited for full session testing — use layer 4 for
that.

---

## 4. End-to-End Against a Real Pi

Uses the same env vars as the rest of the integration test suite:

| Variable | Example |
|----------|---------|
| `PIDBG_TEST_HOST` | `raspberrypi.local` |
| `PIDBG_TEST_USER` | `pi` |
| `PIDBG_TEST_KEY_PATH` | `~/.ssh/pidbg_rsa` |

Tests are skipped when these are absent (CI-friendly). Write `DebugClient`-based tests that
drive the full flow:

```typescript
await dc.launch({ host: process.env.PIDBG_TEST_HOST, ... });
await dc.configurationDoneRequest();
await dc.waitForEvent('stopped');  // breakpoint hit
```

These live in `PiDbg.DebugAdapter.Tests` alongside the C# unit tests.

---

## Recommended Dev Workflow

```
1. Build adapter
   dotnet publish src/PiDbg.DebugAdapter -r win-x64 --self-contained -o src/PiDbg.VsCodeExtension/bin/

2. Compile TypeScript
   cd src/PiDbg.VsCodeExtension && npm run compile

3. Press F5 in VS Code (with PiDbg.VsCodeExtension open)
   → Extension Development Host launches

4. In the host window: open a test .NET project, add launch.json, press F5

5. Iterate: after TypeScript changes → npm run compile → Ctrl+Shift+P "Reload Window"
            after adapter changes   → dotnet publish → restart debug session
```
