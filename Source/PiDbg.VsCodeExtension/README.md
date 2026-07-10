# PiDbg — Raspberry Pi .NET Debugger

Debug .NET applications running on a Raspberry Pi directly from VS Code. PiDbg deploys your project to the Pi over SSH, launches it, and attaches a real debugger — so you get breakpoints, stepping, and variable inspection against code actually running on the device.

## Requirements

- **Windows** host machine (the current debug adapter is built for `win-x64`; other platforms are not yet supported).
- A Raspberry Pi running **Raspberry Pi OS 64-bit (Debian 12, ARM64)** with **.NET 9** installed.
- SSH access to the Pi (key-based auth is strongly recommended over password auth).

## Installation

### From the VS Code Marketplace

Open the **Extensions** view in VS Code (`Ctrl+Shift+X`), search for **PiDbg**, and click **Install**. Alternatively, install directly from the command line:

```sh
code --install-extension wildernessLabs.pidbg
```

### From a downloaded `.vsix` (GitHub Releases)

Every [tagged release](https://github.com/WildernessLabs/pidbg/releases) publishes a `pidbg-vscode-<version>.vsix` file. Download it, then either:

- Run **Extensions: Install from VSIX...** from the Command Palette and select the downloaded file, or
- Install it from the command line:

  ```sh
  code --install-extension pidbg-vscode-<version>.vsix
  ```

## Getting started

Add a debug configuration to your `.vscode/launch.json`. Typing `pidbg` in an empty `launch.json` offers a starter snippet:

```json
{
  "type": "pidbg",
  "request": "launch",
  "name": "Debug on Raspberry Pi",
  "host": "raspberrypi.local",
  "username": "pi",
  "privateKeyPath": "${userHome}/.ssh/pidbg_rsa",
  "appName": "${workspaceFolderBasename}",
  "projectPath": "${workspaceFolder}/${workspaceFolderBasename}.csproj"
}
```

Press **F5** to build, deploy, and attach.

### Configuration properties

| Property | Description |
|---|---|
| `host` | Pi hostname or IP address (required) |
| `username` | SSH username (required) |
| `appName` | Application name, used for the deployment path (required) |
| `projectPath` | Absolute path to the `.csproj` to debug (required) |
| `port` | SSH port (default `22`) |
| `privateKeyPath` | Path to an SSH private key file |
| `password` | SSH password (prefer key-based auth instead) |
| `rootFolder` | Root deployment folder on the Pi (default `~/meadow`) |
| `args` | Arguments passed to the application |
| `deployRuntimeIfNecessary` | Automatically install the required .NET runtime on the device if missing (default `false`). If `false` and the device's .NET runtime doesn't match what the project targets, debugging fails with a message naming both versions instead of installing anything. |
| `stopAtEntry` | Start the app suspended and break before any app code runs, so you can step through startup code (default `false`). |

### Commands

- **PiDbg: Connect to Device** — establish an SSH connection to a configured Pi.
- **PiDbg: Show Output** — open the PiDbg output channel for connection/deployment logs.

## Known limitations

- The debug adapter currently only runs on Windows.
- Password-based SSH auth is supported but discouraged — prefer `privateKeyPath`.

## License

[MIT](./LICENSE)
