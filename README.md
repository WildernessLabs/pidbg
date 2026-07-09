# PiDbg

Debug .NET applications running on a Raspberry Pi directly from your IDE. PiDbg deploys your project to the Pi over SSH, launches it, and attaches [vsdbg](https://github.com/OmniSharp/omnisharp-vscode/wiki/Attaching-to-remote-processes) — so you get real breakpoints, stepping, and variable inspection against code actually running on the device, without leaving Visual Studio or VS Code.

It is not a custom debugger. It orchestrates SSH, deployment, and vsdbg, and hands the debugging itself off to Microsoft's official .NET debugger.

## Components

| Path | What it is |
|---|---|
| `Source/PiDbg.VsCodeExtension` | The VS Code extension. Registers the `pidbg` debug type and launches the debug adapter. **[README](Source/PiDbg.VsCodeExtension/README.md)** |
| `Source/PiDbg.DebugAdapter` | Standalone Debug Adapter Protocol (DAP) server (`pidbg-adapter.exe`) that VS Code launches as a subprocess. Drives the SSH/deploy/attach flow via `PiDbg.Core`, then proxies DAP traffic to vsdbg over an SSH tunnel. |
| `Source/PiDbg.Core` | Shared orchestration library: SSH connect, deploy, start the on-device session, open the vsdbg tunnel. |
| `Source/Meadow.Daemon` | The on-device daemon that runs on the Raspberry Pi (gRPC + REST, ASP.NET Core). Manages deployments and launches vsdbg. |
| `Source/Meadow.Daemon.Contracts` | Shared contracts between the debug adapter and the on-device daemon. |
| `Source/VsExtension` | The Visual Studio 2026 extension (VSIX). |

See [`docs/`](docs/) for deeper architecture notes.

## Requirements

- **Windows** development machine (the debug adapter currently ships as a `win-x64` self-contained executable).
- A Raspberry Pi running **Raspberry Pi OS 64-bit (Debian 12, ARM64)** with **.NET 9** installed.
- SSH access to the Pi (key-based auth strongly recommended over password auth).

## Getting started (VS Code)

See the [VS Code extension README](Source/PiDbg.VsCodeExtension/README.md) for installation (Marketplace or downloadable `.vsix`) and `launch.json` setup.

## Building from source

```powershell
./build.ps1
```

Publishes the debug adapter (`win-x64`, self-contained), compiles the VS Code extension, and packages `dist/pidbg-vscode-<version>.vsix`.

## Release process

Pushing a `v*` tag builds the extension, attaches the `.vsix` to a [GitHub release](../../releases), and publishes it to the VS Code Marketplace. See [`.github/workflows/vscode-package.yml`](.github/workflows/vscode-package.yml).

## License

[MIT](LICENSE)
