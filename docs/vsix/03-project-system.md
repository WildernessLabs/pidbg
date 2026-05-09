# PiDbg VSIX — Project System Integration

---

## 1. CPS and launchSettings.json

Visual Studio 2026 uses the **Common Project System (CPS)** for all SDK-style C# projects.
CPS manages `launchSettings.json` and the debug profile dropdown. PiDbg integrates at
the CPS layer — not the old DTE/project system layer.

The integration requires three MEF exports:
1. `IDebugLaunchProvider` — handles F5 for our profile type
2. `ILaunchSettingsUIProvider` — provides the property page UI
3. `ILaunchSettingsSerializationProvider` — round-trips custom fields in JSON

All three are MEF-exported via the thin MEF→DI bridge pattern (see `01-vsix-architecture.md §3`).

---

## 2. launchSettings.json Schema

### File location
```
<project>/Properties/launchSettings.json
```

### Profile structure
A Raspberry Pi debug profile is stored as a standard `launchSettings.json` profile with
`commandName: "RaspberryPi"`. All PiDbg-specific fields are stored as additional properties
in the profile object. CPS passes these through `ILaunchProfile.OtherSettings`.

```json
{
  "profiles": {
    "Raspberry Pi — Dev Board": {
      "commandName": "RaspberryPi",
      "environmentVariables": {
        "DOTNET_ENVIRONMENT": "Development",
        "MY_APP_KEY": "dev-value"
      },
      "deviceId": "550e8400-e29b-41d4-a716-446655440000",
      "remotePath": "/opt/pidbg/apps/MyApp",
      "selfContained": false,
      "startupArgs": "--config production.json",
      "startupCommand": null,
      "resumeMeadowDaemon": true
    }
  }
}
```

### Field reference

| Field | Type | Required | Default | Notes |
|---|---|---|---|---|
| `commandName` | string | ✓ | — | Must be `"RaspberryPi"` |
| `environmentVariables` | object | | `{}` | Passed to launched process |
| `deviceId` | GUID string | ✓ | — | References `DeviceRecord.Id` |
| `remotePath` | string | | device default | Override `/opt/pidbg/apps/<name>` |
| `selfContained` | bool | | `false` | Self-contained publish (larger deploy) |
| `startupArgs` | string | | `""` | Extra args appended to `dotnet App.dll` |
| `startupCommand` | string? | | `null` | Full command override (rarely needed) |
| `resumeMeadowDaemon` | bool | | `true` | Resume Meadow.Daemon after session |

### Serialization round-trip
CPS serializes `OtherSettings` as top-level properties in the profile JSON object.
Custom fields survive round-trips through VS's "Manage Launch Profiles" dialog only if
we register a `ILaunchSettingsSerializationProvider`. Without this, unknown fields are
dropped on save. Our provider preserves all `deviceId`, `remotePath`, etc. fields.

```csharp
[Export(typeof(ILaunchSettingsSerializationProvider))]
[ExportMetadata("CommandName", RaspberryPiDebugger.CommandName)]
internal class RaspberryPiLaunchSettingsSerializationProvider
    : ILaunchSettingsSerializationProvider
{
    // Keys we own and want preserved:
    private static readonly IReadOnlyList<string> OwnedKeys = new[]
    {
        "deviceId", "remotePath", "selfContained",
        "startupArgs", "startupCommand", "resumeMeadowDaemon"
    };

    public bool IsSettingKey(string settingName)
        => OwnedKeys.Contains(settingName);
}
```

---

## 3. RaspberryPiLaunchProfile — Typed Accessor

`ILaunchProfile.OtherSettings` is `ImmutableDictionary<string, object>`. Reading it
directly everywhere is fragile. `RaspberryPiLaunchProfile` is a typed wrapper:

```csharp
public sealed record RaspberryPiLaunchProfile
{
    public required string ProfileName { get; init; }
    public required Guid DeviceId { get; init; }
    public string? RemotePath { get; init; }
    public bool SelfContained { get; init; }
    public string StartupArgs { get; init; } = string.Empty;
    public string? StartupCommand { get; init; }
    public bool ResumeMeadowDaemon { get; init; } = true;
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }
        = ImmutableDictionary<string, string>.Empty;

    public static RaspberryPiLaunchProfile? From(ILaunchProfile? profile)
    {
        if (profile?.CommandName != RaspberryPiDebugger.CommandName) return null;
        var s = profile.OtherSettings;
        if (!s.TryGetGuid("deviceId", out var deviceId)) return null;
        return new RaspberryPiLaunchProfile
        {
            ProfileName       = profile.Name,
            DeviceId          = deviceId,
            RemotePath        = s.GetString("remotePath"),
            SelfContained     = s.GetBool("selfContained"),
            StartupArgs       = s.GetString("startupArgs") ?? string.Empty,
            StartupCommand    = s.GetString("startupCommand"),
            ResumeMeadowDaemon = s.GetBool("resumeMeadowDaemon", defaultValue: true),
            EnvironmentVariables = profile.EnvironmentVariables
                ?? ImmutableDictionary<string, string>.Empty,
        };
    }
}
```

---

## 4. Property Page UI Provider

The property page is what appears under Project Properties → Debug when the
"Raspberry Pi — Dev Board" profile is selected.

### Registration
```csharp
[Export(typeof(ILaunchSettingsUIProvider))]
[ExportMetadata("CommandName", RaspberryPiDebugger.CommandName)]
internal class RaspberryPiLaunchSettingsUIProviderMef : ILaunchSettingsUIProvider
{
    [Import] internal SVsServiceProvider VsServiceProvider { get; set; } = null!;

    public string CommandName => RaspberryPiDebugger.CommandName;
    public string FriendlyName => "Raspberry Pi";

    public ILaunchSettingsUIExtension? GetCustomUI()
    {
        // Resolve WPF UserControl from DI container
        var container = VsServiceProvider.GetService<PiDbgPackage>().DiContainer;
        return container.GetRequiredService<RaspberryPiPropertyPageView>();
    }
}
```

### Property page fields

```
┌─────────────────────────────────────────────────────────┐
│  Raspberry Pi Debug Profile                             │
│                                                         │
│  Device:     [Dev Board (192.168.1.100) ▼] [Manage...] │
│                                                         │
│  Remote path: [ /opt/pidbg/apps/MyApp        ]         │
│               (leave blank to use device default)       │
│                                                         │
│  Startup arguments: [                        ]         │
│                                                         │
│  Environment variables:                                 │
│  ┌──────────────────────────────────────────────┐      │
│  │  Name                 │  Value               │      │
│  │  DOTNET_ENVIRONMENT   │  Development         │      │
│  │  +                    │                      │      │
│  └──────────────────────────────────────────────┘      │
│                                                         │
│  ☑ Resume Meadow.Daemon after debug session            │
│  ☐ Self-contained publish                               │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

The Device dropdown is populated from `IDeviceRegistry.GetAllDevicesAsync()`.
"Manage..." opens the Device Manager tool window (§04 Device Manager).

---

## 5. IBuildManager Integration

The orchestrator triggers MSBuild via VS's `IBuildManager` (CPS project system):

```csharp
internal sealed class VsBuildService : IVsBuildService
{
    // IBuildManager is obtained from the project's UnconfiguredProject
    public async Task<BuildResult> BuildAndPublishAsync(
        string projectPath,
        string configuration,
        CancellationToken ct)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var solution = (IVsSolution)await _package.GetServiceAsync(typeof(SVsSolution));
        var project = FindProject(solution, projectPath);

        // Switch to background — build is async
        await TaskScheduler.Default;

        var buildResult = await project.GetBuildManagerAsync()
            .BuildAsync(new BuildRequestData(
                projectFile: projectPath,
                globalProperties: new Dictionary<string, string>
                {
                    ["Configuration"]     = configuration,
                    ["RuntimeIdentifier"] = "linux-arm64",
                    ["PublishDir"]        = GetPublishDir(projectPath),
                    ["DebugType"]         = "portable",
                    ["DebugSymbols"]      = "true",
                    ["SelfContained"]     = profile.SelfContained.ToString().ToLower(),
                },
                toolsVersion: null,
                targetsToBuild: new[] { "Publish" }),
            ct);

        return BuildResult.FromMsBuild(buildResult);
    }
}
```

Build output (compiler warnings, errors) flows through the standard VS Error List.
PiDbg does not intercept or filter it.

---

## 6. Multi-Project Support

A solution may have multiple deployable projects (e.g., a backend service and a companion
daemon, both targeting the Pi). Each project has its own `launchSettings.json` with its
own Raspberry Pi profile. Each profile references a `deviceId` independently — two projects
can target the same or different devices.

The VSIX does not restrict the number of profiles per project or the number of projects
per solution. Each F5 press on a project with a Pi profile triggers the full orchestration
for that project's active profile.

**Simultaneous debugging** (debugging two projects to the same Pi at once) is supported
by the agent (different deployment paths, different vsdbg ports). The VSIX ensures
separate SSH connections (or tunnels on a shared connection) per debug session.
Full support is a Phase 6 item.

---

## 7. Project Capability Requirements

The `[AppliesTo]` attribute on the MEF exports restricts the launch provider to projects
with the correct capabilities:

```csharp
// Applies to C# projects with .NET SDK (excludes VB, F#, old csproj format)
[AppliesTo(ProjectCapabilities.CSharp + " & " + ProjectCapability.DotNet)]
```

Non-SDK projects (old `.csproj` format) are explicitly unsupported. If a user with an
old-format project installs the VSIX, the Raspberry Pi profile type does not appear in
their debug dropdown. An informational message is shown in the Output window if such a
project is detected opening a `.pidbg` settings file.
