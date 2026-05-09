# PiDbg VSIX — Extension Manifest and Schema

---

## 1. source.extension.vsixmanifest

```xml
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0"
    xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011"
    xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">

  <Metadata>
    <Identity
        Id="PiDbg.VisualStudio"
        Version="1.0.0"
        Language="en-US"
        Publisher="Wilderness Labs" />

    <DisplayName>PiDbg — Raspberry Pi Remote Debugger</DisplayName>

    <Description xml:space="preserve">
      Debug .NET 10 applications running on Raspberry Pi ARM64 devices
      directly from Visual Studio. Supports F5 launch debugging, breakpoints,
      stepping, watch windows, locals, and call stacks over SSH.
    </Description>

    <MoreInfo>https://github.com/WildernessLabs/pidbg</MoreInfo>
    <License>LICENSE.txt</License>
    <Icon>Resources\pidbg_128.png</Icon>
    <PreviewImage>Resources\pidbg_preview.png</PreviewImage>

    <Tags>raspberry pi, remote debug, arm64, iot, dotnet, ssh</Tags>

    <GettingStartedGuide>
      https://github.com/WildernessLabs/pidbg/wiki/getting-started
    </GettingStartedGuide>

    <ReleaseNotes>
      https://github.com/WildernessLabs/pidbg/releases
    </ReleaseNotes>
  </Metadata>

  <Installation>
    <!-- Visual Studio 2026 minimum -->
    <InstallationTarget Id="Microsoft.VisualStudio.Community"
        Version="[18.0, 19.0)" />
    <InstallationTarget Id="Microsoft.VisualStudio.Professional"
        Version="[18.0, 19.0)" />
    <InstallationTarget Id="Microsoft.VisualStudio.Enterprise"
        Version="[18.0, 19.0)" />
  </Installation>

  <Dependencies>
    <!-- Required: Common Project System (for C# project integration) -->
    <Dependency Id="Microsoft.VisualStudio.Component.Roslyn.LanguageServices"
        DisplayName="C# and Visual Basic"
        Version="[18.0, 19.0)" />

    <!-- Required: .NET desktop development workload (for MSBuild publish) -->
    <Dependency Id="Microsoft.VisualStudio.Workload.ManagedDesktop"
        DisplayName=".NET desktop development"
        Version="[18.0, 19.0)"
        d:Source="Installed" />
  </Dependencies>

  <Prerequisites>
    <!-- Minimum Windows version: Windows 10 (for WinHttpHandler HTTP/2) -->
    <Prerequisite Id="Microsoft.VisualStudio.Component.CoreEditor"
        Version="[18.0, 19.0)"
        DisplayName="Visual Studio core shell" />
  </Prerequisites>

  <Assets>
    <!-- The extension package itself -->
    <Asset Type="Microsoft.VisualStudio.VsPackage"
        d:Source="Project"
        d:ProjectName="%CurrentProject%"
        Path="|%CurrentProject%;PkgdefProjectOutputGroup|" />

    <!-- MEF exports (launch provider, property page, etc.) -->
    <Asset Type="Microsoft.VisualStudio.MefComponent"
        d:Source="Project"
        d:ProjectName="%CurrentProject%"
        Path="|%CurrentProject%|" />

    <!-- Tool window registration -->
    <Asset Type="Microsoft.VisualStudio.ToolboxControl"
        d:Source="Project"
        d:ProjectName="%CurrentProject%"
        Path="|%CurrentProject%|" />
  </Assets>

</PackageManifest>
```

**Note on version range**: VS 2026 is expected to be version 18.x. The manifest targets
`[18.0, 19.0)`. Adjust if Microsoft's numbering changes. The VSIX will refuse to install
on VS 2022 (17.x) or older — this is intentional since the debug APIs differ.

---

## 2. PiDbgPackage.pkgdef

The `.pkgdef` file registers the package's tool windows and commands with the VS shell.
Generated automatically from attributes, but key entries:

```
// Register the PiDbg output pane GUID
[$RootKey$\ToolWindows\{A1B2C3D4-AAAA-BBBB-CCCC-000000000001}]
@="PiDbg — Remote Devices"
"DontForceCreate"=dword:00000001
"Orientation"=dword:00000003

[$RootKey$\ToolWindows\{A1B2C3D4-AAAA-BBBB-CCCC-000000000002}]
@="PiDbg Log Viewer"
"DontForceCreate"=dword:00000001

// Register the output pane
[$RootKey$\OutputWindow\{A1B2C3D4-AAAA-BBBB-CCCC-000000000003}]
@="PiDbg"
```

---

## 3. RaspberryPiDebugger Constants

```csharp
internal static class RaspberryPiDebugger
{
    // The commandName value in launchSettings.json
    public const string CommandName = "RaspberryPi";

    // Friendly name shown in the debug dropdown
    public const string FriendlyName = "Raspberry Pi";

    // The managed .NET Core debug engine GUID
    // Stable across VS versions since VS 2019
    public static readonly Guid ManagedDebugEngineGuid =
        new("2E36F1D4-B23C-435D-AB41-18E608940038");

    // vsdbg port range on the Pi (127.0.0.1 only)
    public const int VsdbgPortRangeStart = 4024;
    public const int VsdbgPortRangeEnd   = 4124;

    // Agent gRPC port on the Pi (127.0.0.1 only)
    public const int AgentGrpcPort = 50051;

    // Default remote deploy root
    public const string DefaultRemoteDeployRoot = "/opt/pidbg/apps";
}
```

---

## 4. launchSettings.json — Full Example

A project with two profiles: one for debug (F5) and one for a headless run without
debug (Ctrl+F5):

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "MyApp (Raspberry Pi — Debug)": {
      "commandName": "RaspberryPi",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development",
        "DOTNET_SYSTEM_CONSOLE_ALLOW_ANSI_COLOR_REDIRECTION": "1"
      },
      "deviceId": "550e8400-e29b-41d4-a716-446655440000",
      "remotePath": "/opt/pidbg/apps/MyApp",
      "selfContained": false,
      "startupArgs": "--log-level Debug",
      "startupCommand": null,
      "resumeMeadowDaemon": true
    },
    "MyApp (Raspberry Pi — Release)": {
      "commandName": "RaspberryPi",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Production"
      },
      "deviceId": "550e8400-e29b-41d4-a716-446655440000",
      "remotePath": "/opt/pidbg/apps/MyApp",
      "selfContained": false,
      "startupArgs": "",
      "startupCommand": null,
      "resumeMeadowDaemon": false
    },
    "MyApp (Local)": {
      "commandName": "Project"
    }
  }
}
```

Notes:
- Multiple Pi profiles can target the same or different devices
- The standard `"commandName": "Project"` local profile coexists normally
- `environmentVariables` is standard `launchSettings.json` — VS merges it with
  process environment before passing to our launch provider

---

## 5. Class Diagram — Debug Integration

```
PiDbgPackage (AsyncPackage)
│ ├── DiContainer: IServiceProvider
│ └── JoinableTaskFactory

RaspberryPiLaunchProviderMef [MEF]
│ └── resolves→ RaspberryPiLaunchProvider
│                └── DebugSessionOrchestrator
│                     ├── IDeviceConnectionFactory
│                     │   └── DeviceConnectionFactory
│                     │        ├── ISshConnectionManager (per device)
│                     │        └── IAgentClient (per device)
│                     ├── IVsBuildService
│                     │   └── VsBuildService
│                     ├── IDeploymentService
│                     │   └── DeploymentService
│                     │        ├── IDeploymentPackager
│                     │        └── ISftpTransferService
│                     ├── IVsOutputWindowService
│                     └── ITelemetryService

RaspberryPiLaunchSettingsUIProviderMef [MEF]
│ └── resolves→ RaspberryPiPropertyPageView (WPF UserControl)
│                └── RaspberryPiPropertyPageViewModel
│                     └── IDeviceRegistry

RaspberryPiLaunchSettingsSerializationProviderMef [MEF]
    └── resolves→ RaspberryPiLaunchSettingsSerializationProvider
```

---

## 6. Class Diagram — Device Manager

```
DeviceManagerWindow (ToolWindowPane)
└── DeviceManagerWindowControl (WPF UserControl)
     └── DeviceManagerViewModel
          ├── Devices: ObservableCollection<DeviceItemViewModel>
          │   └── DeviceItemViewModel
          │        ├── DeviceRecord (immutable, from IDeviceRegistry)
          │        ├── ConnectionState: enum {Unknown,Testing,Connected,Failed}
          │        └── AgentStatus?: AgentStatus (from last ping)
          ├── SelectedDevice: DeviceItemViewModel?
          ├── AddDeviceCommand → AddEditDeviceDialog
          ├── EditDeviceCommand → AddEditDeviceDialog (pre-filled)
          ├── RemoveDeviceCommand
          ├── TestConnectionCommand → SshDeviceProber
          ├── ProvisionDeviceCommand → ProvisioningService
          ├── DeployCommand → DeploymentService (no debug)
          └── ShowLogsCommand → LogViewerWindow

AddEditDeviceDialog (Window)
└── AddEditDeviceViewModel
     ├── FriendlyName, Host, Port, Username: string
     ├── AuthMethod: enum {SshKey, Password}
     ├── SshKeyPath, Password: string
     ├── DefaultDeployPath, StartupCommandOverride: string
     ├── TestConnectionCommand → SshDeviceProber
     ├── ProbeResult: DeviceCapabilities?
     └── SaveCommand → IDeviceRegistry.AddDeviceAsync / UpdateDeviceAsync
```

---

## 7. Interface Summary

Complete list of interfaces defined in `PiDbg.Vsix`:

```csharp
// Debug integration
interface IDebugSessionOrchestrator
interface IVsDebuggerAttacher          // wraps IVsDebugger4
interface IVsBuildService

// Device management (VSIX-side)
interface IVsOutputWindowService
interface IVsStatusBarService
interface IVsInfoBarService
interface ICredentialService
interface ITelemetryService
interface IVsThreadingService          // JoinableTaskFactory access

// Deployment orchestration (VSIX-side)
// Implemented in PiDbg.Deployment referenced library:
interface IDeploymentService           // (doc 04)
interface IDeploymentPackager          // (doc 04)
```

Interfaces shared with `PiDbg.DeviceManagement`, `PiDbg.Transport`, and
`PiDbg.Contracts` are defined in those libraries (see `docs/04-interfaces.md`).
