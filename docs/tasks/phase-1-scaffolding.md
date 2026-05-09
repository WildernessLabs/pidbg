# Phase 1 — Repository Scaffolding, Shared Libraries, Logging, Configuration

All tasks in this phase are prerequisites for every subsequent phase. Complete them in order;
P1.3 and P1.4 depend on P1.2, everything else depends on P1.1.

---

## P1.1 — Repo Root Configuration Files

**Purpose**: Establish consistent code style, line endings, and build behavior across all
projects before any source is written.

**Dependencies**: None.

**Files**:
- `.gitignore` — root gitignore
- `.gitattributes` — line-ending policy
- `.editorconfig` — code style rules
- `Directory.Build.props` — MSBuild defaults for all projects

**Implementation details**:

`.gitignore` must cover:
- Standard .NET patterns: `bin/`, `obj/`, `*.user`, `*.suo`, `.vs/`, `*.nupkg`
- VSIX patterns: `*.vsix` (build output only; committed vsix is in `artifacts/`)
- Publish output: `publish/`, `artifacts/`
- OS patterns: `.DS_Store`, `Thumbs.db`, `desktop.ini`
- PiDbg-specific: `Source/VsExtension/PkgDefOutputPath/`, `Source/VsExtension/obj/`

`.gitattributes` must set:
```
* text=auto
*.sh  text eol=lf
*.proto text eol=lf
*.service text eol=lf
*.json text eol=lf
*.md  text eol=lf
*.cs  text eol=crlf
*.csproj text eol=crlf
*.slnx text eol=crlf
*.vsct text eol=crlf
*.png binary
*.vsix binary
```
Shell scripts and proto files must always have LF endings or they will fail on Linux.

`Directory.Build.props` must contain:
```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisMode>Recommended</AnalysisMode>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

`.editorconfig` must define:
- `indent_style = space`, `indent_size = 4` for `*.cs`
- `indent_style = space`, `indent_size = 2` for `*.json`, `*.proto`, `*.yml`
- `charset = utf-8` for all files
- `trim_trailing_whitespace = true`
- `insert_final_newline = true`
- C# specific: `dotnet_sort_system_directives_first = true`,
  `csharp_new_line_before_open_brace = all`

**Edge cases**:
- `TreatWarningsAsErrors=true` will break on generated proto files if they emit warnings.
  Add `<NoWarn>$(NoWarn);1591</NoWarn>` in the Contracts project (missing XML doc).
- `eol=lf` in `.gitattributes` only normalises on commit; existing checked-in files keep
  their endings until touched. Run `git add --renormalize .` after adding `.gitattributes`.

**Testing requirements**:
- `git diff --check` reports no whitespace errors after normalisation
- Open any `.cs` file in VS and verify EditorConfig is detected (bottom status bar shows
  "EditorConfig")
- Create a test `.sh` file, commit, and verify `file -b` reports `ASCII text` not
  `ASCII text, with CRLF line terminators` on Linux

**Definition of done**:
- [ ] `.gitignore` exists and ignores `bin/`, `obj/`, `.vs/`, `*.user`
- [ ] `.gitattributes` exists with LF rules for `.sh`, `.proto`, `.service`
- [ ] `.editorconfig` exists with C# and JSON rules
- [ ] `Directory.Build.props` exists with Nullable, ImplicitUsings, TreatWarningsAsErrors
- [ ] `git status` shows no untracked config files after committing the above

---

## P1.2 — Central Package Management

**Purpose**: Pin all NuGet package versions in one place so every project uses identical
versions and version drift across projects is impossible.

**Dependencies**: P1.1

**Files**:
- `Directory.Packages.props` — central version catalog

**Implementation details**:

`Directory.Packages.props` structure:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <!-- Grpc / Protobuf -->
  <ItemGroup>
    <PackageVersion Include="Google.Protobuf" Version="3.29.3" />
    <PackageVersion Include="Grpc.Tools" Version="2.70.0">
      <PrivateAssets>all</PrivateAssets>
    </PackageVersion>
    <PackageVersion Include="Grpc.Net.Client" Version="2.70.0" />
    <PackageVersion Include="Grpc.AspNetCore" Version="2.70.0" />
    <PackageVersion Include="Grpc.HealthCheck" Version="2.70.0" />
  </ItemGroup>

  <!-- Hosting -->
  <ItemGroup>
    <PackageVersion Include="Microsoft.Extensions.Hosting.Systemd" Version="10.0.0" />
    <PackageVersion Include="Microsoft.Extensions.Options.DataAnnotations" Version="10.0.0" />
  </ItemGroup>

  <!-- SSH -->
  <ItemGroup>
    <PackageVersion Include="SSH.NET" Version="2024.2.0" />
  </ItemGroup>

  <!-- MQTT -->
  <ItemGroup>
    <PackageVersion Include="MQTTnet" Version="5.0.1" />
  </ItemGroup>

  <!-- Linux interop -->
  <ItemGroup>
    <PackageVersion Include="Mono.Posix.NETStandard" Version="1.0.0" />
  </ItemGroup>
</Project>
```

In each project `.csproj`, `<PackageReference>` items must omit the `Version` attribute —
the version comes from `Directory.Packages.props`. Example:
```xml
<PackageReference Include="Google.Protobuf" />
```

`Grpc.Tools` must always have `<PrivateAssets>all</PrivateAssets>` in the catalog entry
so it is a build-time tool and does not become a runtime dependency.

**Edge cases**:
- `Grpc.Tools` version must exactly match `Grpc.Net.Client` / `Grpc.AspNetCore` version or
  you get generated code that is incompatible with the runtime. Keep them identical.
- .NET 10 ships its own ASP.NET Core packages; do not add explicit framework package refs
  (no `Microsoft.AspNetCore.*` in `<PackageVersion>` — use `<FrameworkReference>` instead).
- `Mono.Posix.NETStandard` must be conditionally referenced:
  ```xml
  <PackageReference Include="Mono.Posix.NETStandard"
                    Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
  ```
  Add the same condition in `Directory.Packages.props` or accept that the package
  is downloaded but unused on Windows CI.

**Testing requirements**:
- `dotnet restore pidbg.slnx` exits 0 with no version conflict warnings
- `dotnet list pidbg.slnx package --outdated` runs without error
- Manually verify: open any project `.csproj` and confirm no `Version` attributes on
  `PackageReference` items

**Definition of done**:
- [ ] `Directory.Packages.props` exists at repo root
- [ ] `ManagePackageVersionsCentrally=true`
- [ ] All packages used across all three projects are listed with pinned versions
- [ ] `Grpc.Tools` has `PrivateAssets=all`
- [ ] `dotnet restore` exits 0 with no warnings

---

## P1.3 — Meadow.Daemon.Contracts Project

**Purpose**: Configure the shared contracts project so proto files compile to correct C#
types and the generated service base classes are available to both the daemon and the VSIX.

**Dependencies**: P1.2

**Files**:
- `Source/Meadow.Daemon.Contracts/Meadow.Daemon.Contracts.csproj`
- `Source/Meadow.Daemon.Contracts/proto/common.proto`
- `Source/Meadow.Daemon.Contracts/proto/deployment.proto`
- `Source/Meadow.Daemon.Contracts/proto/process.proto`
- `Source/Meadow.Daemon.Contracts/proto/session.proto`
- `Source/Meadow.Daemon.Contracts/proto/meadow_daemon.proto`

**Implementation details**:

`.csproj` content:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;netstandard2.1</TargetFrameworks>
    <RootNamespace>Meadow.Daemon.Contracts</RootNamespace>
    <AssemblyName>Meadow.Daemon.Contracts</AssemblyName>
    <!-- suppress missing XML docs warning from generated code -->
    <NoWarn>$(NoWarn);1591</NoWarn>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Google.Protobuf" />
    <PackageReference Include="Grpc.Tools" />
    <PackageReference Include="Grpc.Net.Client" />
  </ItemGroup>

  <ItemGroup>
    <Protobuf Include="proto\common.proto"    GrpcServices="Both" />
    <Protobuf Include="proto\deployment.proto" GrpcServices="Both" />
    <Protobuf Include="proto\process.proto"   GrpcServices="Both" />
    <Protobuf Include="proto\session.proto"   GrpcServices="Both" />
    <Protobuf Include="proto\meadow_daemon.proto" GrpcServices="Both" />
  </ItemGroup>
</Project>
```

Each proto file must begin with:
```proto
syntax = "proto3";
package meadow.daemon.v1;
option csharp_namespace = "Meadow.Daemon.Contracts.V1";
```

Verify the following types are generated after `dotnet build`:
- `Meadow.Daemon.Contracts.V1.MeadowDaemonService.MeadowDaemonServiceBase` (abstract server base)
- `Meadow.Daemon.Contracts.V1.MeadowDaemonService.MeadowDaemonServiceClient` (client stub)
- `Meadow.Daemon.Contracts.V1.PingRequest`, `PongResponse`
- `Meadow.Daemon.Contracts.V1.DeploymentManifest`, `FileEntry`
- `Meadow.Daemon.Contracts.V1.SessionState`, `SessionMode` enums
- `Meadow.Daemon.Contracts.V1.AppState` enum

All proto `import` statements must use relative paths within the `proto/` directory:
```proto
import "common.proto";
import "deployment.proto";
```
The Grpc.Tools protoc compiler resolves imports relative to the project root by default;
set `<Protobuf ProtoRoot="proto\" ...>` if imports fail to resolve.

**Edge cases**:
- `GrpcServices="Both"` generates both client and server stubs. If only server is needed
  in the daemon, `GrpcServices="Server"` would reduce binary size — but `Both` is safer for
  the integration test harness which needs the client.
- Multi-targeting `net10.0;netstandard2.1` means the VSIX (which may target an older
  framework) can reference the `netstandard2.1` TFM. Verify the VSIX project sees the
  correct TFM when it adds a project reference.
- Proto files that import each other must be listed in dependency order, or use
  `AdditionalProtoPathDirs` MSBuild property to specify the import search path.

**Testing requirements**:
- `dotnet build Source/Meadow.Daemon.Contracts/Meadow.Daemon.Contracts.csproj` exits 0
- `obj/` directory contains generated `*.cs` files for all 5 proto files
- `grep -r "MeadowDaemonServiceBase" obj/` finds the generated base class
- No CS1591 (missing XML docs) warnings bubble through

**Definition of done**:
- [ ] `.csproj` references all 5 proto files with `GrpcServices="Both"`
- [ ] `dotnet build` generates C# types in `obj/`
- [ ] All expected types are present in generated output
- [ ] Build produces zero errors and zero warnings
- [ ] Multi-target `net10.0;netstandard2.1` compiles for both TFMs

---

## P1.4 — Meadow.Daemon Project Configuration

**Purpose**: Configure the daemon project with the correct runtime targets, trim settings,
and package references so `dotnet publish -r linux-arm64` produces a working self-contained
single-file binary.

**Dependencies**: P1.2, P1.3

**Files**:
- `Source/Meadow.Daemon/Meadow.Daemon.csproj`
- `Source/Meadow.Daemon/TrimmerRoots.xml`

**Implementation details**:

`.csproj` content:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RuntimeIdentifier>linux-arm64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <PublishTrimmed>true</PublishTrimmed>
    <TrimmerRootDescriptor>TrimmerRoots.xml</TrimmerRootDescriptor>
    <EnableTrimAnalyzer>true</EnableTrimAnalyzer>
    <RootNamespace>Meadow.Daemon</RootNamespace>
    <AssemblyName>meadow-daemon</AssemblyName>
    <!-- binary is named meadow-daemon not Meadow.Daemon -->
  </PropertyGroup>

  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Systemd" />
    <PackageReference Include="Grpc.AspNetCore" />
    <PackageReference Include="Grpc.HealthCheck" />
    <PackageReference Include="MQTTnet" />
    <PackageReference Include="Mono.Posix.NETStandard"
                      Condition="$([MSBuild]::IsOSPlatform('Linux'))" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Meadow.Daemon.Contracts\Meadow.Daemon.Contracts.csproj" />
  </ItemGroup>
</Project>
```

`TrimmerRoots.xml` must preserve gRPC reflection types and JSON serialization types:
```xml
<linker>
  <!-- Preserve gRPC generated types -->
  <assembly fullname="Meadow.Daemon.Contracts">
    <type fullname="*" preserve="all" />
  </assembly>
  <!-- Preserve ASP.NET Core routing, gRPC internals -->
  <assembly fullname="Grpc.AspNetCore.Server" preserve="all" />
  <assembly fullname="Google.Protobuf" preserve="all" />
  <!-- Preserve System.Text.Json serialization for models -->
  <assembly fullname="Meadow.Daemon">
    <namespace fullname="Meadow.Daemon.Models" preserve="all" />
  </assembly>
</linker>
```

**Edge cases**:
- `PublishTrimmed=true` will trim away reflection-heavy code paths in gRPC. The
  `TrimmerRoots.xml` prevents this, but run `dotnet publish` with
  `--verbosity detailed 2>&1 | grep "ILLink"` to see what is being trimmed.
- `AssemblyName=meadow-daemon` (with hyphen) produces a binary named `meadow-daemon` on
  Linux, which is the expected binary name in the service template.
- `RuntimeIdentifier=linux-arm64` in the project file means `dotnet build` on Windows will
  cross-compile by default. For local Windows development builds, add a
  `Directory.Build.props` override or use `-r` only during publish.
- `Microsoft.NET.Sdk.Web` is required (not `Microsoft.NET.Sdk`) because Kestrel and
  ASP.NET Core middleware are needed for gRPC.

**Testing requirements**:
- `dotnet build Source/Meadow.Daemon/Meadow.Daemon.csproj` exits 0
- `dotnet publish Source/Meadow.Daemon/Meadow.Daemon.csproj -r linux-arm64 -c Release`
  produces a single file named `meadow-daemon` in `publish/`
- `file publish/meadow-daemon` reports `ELF 64-bit LSB executable, ARM aarch64`
- `wc -c publish/meadow-daemon` shows size between 20 MB and 60 MB (sanity check)

**Definition of done**:
- [ ] `.csproj` uses `Microsoft.NET.Sdk.Web`
- [ ] `RuntimeIdentifier=linux-arm64`, `SelfContained=true`, `PublishSingleFile=true`
- [ ] `TrimmerRoots.xml` preserves Contracts assembly and Models namespace
- [ ] `dotnet build` exits 0
- [ ] `dotnet publish -r linux-arm64` produces an ARM64 ELF binary
- [ ] Binary name is `meadow-daemon` (with hyphen)

---

## P1.5 — DaemonPaths Static Helper

**Purpose**: Centralise every filesystem path the daemon uses so no path string is
hardcoded in more than one place.

**Dependencies**: P1.4, P1.6 (needs DaemonOptions)

**Files**:
- `Source/Meadow.Daemon/DaemonPaths.cs`

**Implementation details**:

```csharp
// All methods are static and pure — given the same options they return the same path.
// Callers do not mutate these paths; they call EnsureDirectories once at startup.
public static class DaemonPaths
{
    // Daemon binary
    public static string BinDir(DaemonOptions o)        => Path.Combine(o.InstallRoot, "bin");
    public static string BinPath(DaemonOptions o)       => Path.Combine(BinDir(o), "meadow-daemon");
    public static string BinBackupPath(DaemonOptions o) => Path.Combine(BinDir(o), "meadow-daemon.bak");

    // App trees
    public static string AppsDir(DaemonOptions o)       => o.AppRoot;
    public static string AppDir(DaemonOptions o, string appName)
        => Path.Combine(o.AppRoot, SanitizeName(appName));
    public static string AppDebugDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "debug");
    public static string AppStagingDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "staging");
    public static string AppVersionsDir(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "versions");
    public static string AppVersionDir(DaemonOptions o, string appName, string versionId)
        => Path.Combine(AppVersionsDir(o, appName), SanitizeName(versionId));
    public static string AppActiveSymlink(DaemonOptions o, string appName)
        => Path.Combine(AppDir(o, appName), "active");
    public static string AppLocksDir(DaemonOptions o)
        => Path.Combine(o.AppRoot, ".locks");
    public static string AppManifestPath(DaemonOptions o, string appName, string versionId)
        => Path.Combine(AppVersionDir(o, appName, versionId), "manifest.json");

    // vsdbg
    public static string VsdbgDir(DaemonOptions o)       => o.VsdbgRoot;
    public static string VsdbgBinPath(DaemonOptions o)   => Path.Combine(o.VsdbgRoot, "vsdbg-ui");
    public static string VsdbgVersionFile(DaemonOptions o) => Path.Combine(o.VsdbgRoot, ".version");

    // State
    public static string StateDir(DaemonOptions o)       => o.StateRoot;
    public static string AppsStatePath(DaemonOptions o)  => Path.Combine(o.StateRoot, "apps.json");
    public static string SessionsStatePath(DaemonOptions o) => Path.Combine(o.StateRoot, "sessions.json");

    // Logs
    public static string LogDir(DaemonOptions o) => o.LogRoot;

    // Temp
    public static string TempDir() => Path.Combine(Path.GetTempPath(), "meadow-daemon");

    // Creates all base directories that must exist at startup.
    // Call once from IHostedService.StartAsync or Program.cs.
    public static void EnsureDirectories(DaemonOptions o)
    {
        Directory.CreateDirectory(BinDir(o));
        Directory.CreateDirectory(AppsDir(o));
        Directory.CreateDirectory(AppLocksDir(o));
        Directory.CreateDirectory(VsdbgDir(o));
        Directory.CreateDirectory(StateDir(o));
        Directory.CreateDirectory(LogDir(o));
        Directory.CreateDirectory(TempDir());
    }

    // Prevents directory traversal: app names and version IDs must be safe path components.
    public static string SanitizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty", nameof(name));
        // Allow alphanumeric, hyphen, underscore, dot — reject everything else
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9._-]+$"))
            throw new ArgumentException($"Invalid name '{name}': only [a-zA-Z0-9._-] allowed", nameof(name));
        // Prevent traversal
        if (name.Contains("..") || name.StartsWith('.'))
            throw new ArgumentException($"Invalid name '{name}': traversal not allowed", nameof(name));
        return name;
    }
}
```

**Edge cases**:
- `SanitizeName` is the security boundary. Every method that takes `appName` or `versionId`
  must pass through it. Callers must never concatenate raw gRPC input into paths directly.
- `TempDir()` returns a fixed path under the system temp. It must be created on startup and
  does NOT take `DaemonOptions` — it is always in system temp regardless of options.
- `EnsureDirectories` is idempotent: `Directory.CreateDirectory` is a no-op if the
  directory already exists.
- On Windows (developer builds), all paths work correctly because `Path.Combine` is
  cross-platform. Do not use `/` literals in any path construction.

**Testing requirements**:
- Unit test: `SanitizeName("my-app")` → `"my-app"` (valid)
- Unit test: `SanitizeName("../etc/passwd")` → throws `ArgumentException`
- Unit test: `SanitizeName("")` → throws `ArgumentException`
- Unit test: `SanitizeName(".hidden")` → throws `ArgumentException`
- Unit test: `SanitizeName("app name with spaces")` → throws `ArgumentException`
- Unit test: All path methods return paths that start with the configured root

**Definition of done**:
- [ ] `DaemonPaths.cs` exists with all path methods listed above
- [ ] `SanitizeName` validates and rejects directory traversal patterns
- [ ] `EnsureDirectories` is idempotent
- [ ] Unit tests pass for `SanitizeName` valid and invalid inputs
- [ ] No raw string concatenation of user input in any path method

---

## P1.6 — DaemonOptions Configuration Class

**Purpose**: Define the strongly-typed configuration class with validation so misconfigured
deployments fail at startup with a clear error rather than at runtime with a null reference.

**Dependencies**: P1.4

**Files**:
- `Source/Meadow.Daemon/Services/DaemonOptions.cs` (already exists as stub — complete it)

**Implementation details**:

```csharp
using System.ComponentModel.DataAnnotations;

namespace Meadow.Daemon.Services;

public sealed class DaemonOptions
{
    public const string Section = "Daemon";

    // Network
    [Range(1, 65535)]
    public int GrpcPort { get; init; } = 50051;

    [Range(1, 65535)]
    public int RestPort { get; init; } = 5000;

    // Filesystem roots (must be absolute paths)
    [Required]
    public string InstallRoot { get; init; } = "/opt/meadow";

    [Required]
    public string AppRoot { get; init; } = "/opt/meadow/apps";

    [Required]
    public string VsdbgRoot { get; init; } = "/opt/meadow/vsdbg";

    [Required]
    public string StateRoot { get; init; } = "/opt/meadow/state";

    [Required]
    public string LogRoot { get; init; } = "/opt/meadow/logs";

    // Deployment
    [Range(1, 20)]
    public int DeploymentRetentionCount { get; init; } = 3;

    // vsdbg port range
    [Range(1024, 65535)]
    public int VsdbgPortRangeStart { get; init; } = 4024;

    [Range(1024, 65535)]
    public int VsdbgPortRangeEnd { get; init; } = 4124;

    // Process lifecycle
    [Range(1, 60)]
    public int ProcessGracefulStopSeconds { get; init; } = 5;

    public bool AutoRestartManagedApp { get; init; } = true;

    [Range(1, 1440)]
    public int DebugSessionOrphanTimeoutMinutes { get; init; } = 30;

    // Computed TimeSpan helpers (not bound from config)
    public TimeSpan ProcessGracefulStopTimeout
        => TimeSpan.FromSeconds(ProcessGracefulStopSeconds);

    public TimeSpan DebugSessionOrphanTimeout
        => TimeSpan.FromMinutes(DebugSessionOrphanTimeoutMinutes);

    // Validation beyond DataAnnotations
    public IEnumerable<string> GetValidationErrors()
    {
        if (VsdbgPortRangeEnd <= VsdbgPortRangeStart)
            yield return "VsdbgPortRangeEnd must be greater than VsdbgPortRangeStart";

        foreach (var root in new[] { InstallRoot, AppRoot, VsdbgRoot, StateRoot, LogRoot })
        {
            if (!Path.IsPathRooted(root))
                yield return $"Path '{root}' must be an absolute path";
        }
    }
}
```

Register with validation in `Program.cs`:
```csharp
builder.Services
    .AddOptions<DaemonOptions>()
    .BindConfiguration(DaemonOptions.Section)
    .ValidateDataAnnotations()
    .ValidateOnStart();
```

**Edge cases**:
- `ValidateOnStart()` means the app refuses to start if options are invalid. This is the
  correct behaviour — better to crash at startup with a clear message than fail silently
  later.
- `init` setters make options immutable after binding. Do not change to `set` — the
  immutability is intentional.
- `GetValidationErrors()` supplements `DataAnnotations` for cross-field rules. Call it
  from a custom `IValidateOptions<DaemonOptions>` implementation registered as a service.

**Testing requirements**:
- Unit test: valid options pass `GetValidationErrors()` with empty result
- Unit test: `VsdbgPortRangeEnd <= VsdbgPortRangeStart` → validation error
- Unit test: relative path in `AppRoot` → validation error
- Integration test: daemon refuses to start with a missing `AppRoot` value

**Definition of done**:
- [ ] All properties have defaults matching the design docs
- [ ] `[Required]` and `[Range]` attributes on all appropriate properties
- [ ] `GetValidationErrors()` catches cross-field invariants
- [ ] Registered with `ValidateOnStart()` in `Program.cs`
- [ ] TimeSpan computed properties present and tested

---

## P1.7 — Configuration Binding and appsettings.json

**Purpose**: Define the default configuration values, the configuration source chain, and
the environment variable override mechanism.

**Dependencies**: P1.6

**Files**:
- `Source/Meadow.Daemon/appsettings.json`
- `Source/Meadow.Daemon/appsettings.Development.json`

**Implementation details**:

`appsettings.json` (production defaults):
```json
{
  "Daemon": {
    "GrpcPort": 50051,
    "RestPort": 5000,
    "InstallRoot": "/opt/meadow",
    "AppRoot": "/opt/meadow/apps",
    "VsdbgRoot": "/opt/meadow/vsdbg",
    "StateRoot": "/opt/meadow/state",
    "LogRoot": "/opt/meadow/logs",
    "DeploymentRetentionCount": 3,
    "VsdbgPortRangeStart": 4024,
    "VsdbgPortRangeEnd": 4124,
    "ProcessGracefulStopSeconds": 5,
    "AutoRestartManagedApp": true,
    "DebugSessionOrphanTimeoutMinutes": 30
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Grpc": "Warning"
    }
  }
}
```

`appsettings.Development.json` (developer overrides — not deployed to Pi):
```json
{
  "Daemon": {
    "AppRoot": "/tmp/meadow-dev/apps",
    "VsdbgRoot": "/tmp/meadow-dev/vsdbg",
    "StateRoot": "/tmp/meadow-dev/state",
    "LogRoot": "/tmp/meadow-dev/logs",
    "InstallRoot": "/tmp/meadow-dev"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Meadow.Daemon": "Trace"
    }
  }
}
```

Configuration source chain in `Program.cs`:
```csharp
builder.Configuration
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true)
    .AddJsonFile("/etc/meadow/daemon.conf", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables(prefix: "MEADOW_");
```

Environment variable mapping: `MEADOW_DAEMON__GRPCPORT=50051` maps to
`Daemon:GrpcPort` (double underscore is the section separator).

**Edge cases**:
- `/etc/meadow/daemon.conf` is optional — the daemon must start without it. If it
  exists with invalid JSON, the daemon should fail at startup with a clear parse error
  (this is the default `AddJsonFile` behaviour).
- `reloadOnChange: false` for the external config file — live config reload is not
  supported. Changes require a service restart.
- `appsettings.Development.json` must be in `.gitignore` if it contains secrets,
  but in this case it contains only path overrides, so it is safe to commit.
- `.AddEnvironmentVariables("MEADOW_")` is last in the chain so it always wins.
  Document this priority order in a comment in `Program.cs`.

**Testing requirements**:
- Integration test: start daemon with `MEADOW_DAEMON__GRPCPORT=51000` env var set,
  verify it listens on port 51000
- Unit test: `IConfiguration.GetSection("Daemon").Bind(options)` populates all fields
  from `appsettings.json` defaults

**Definition of done**:
- [ ] `appsettings.json` contains all `DaemonOptions` properties with correct defaults
- [ ] `appsettings.Development.json` redirects paths to `/tmp/meadow-dev/`
- [ ] Configuration chain is ordered: json → external conf → env vars
- [ ] `MEADOW_DAEMON__` prefix correctly overrides options
- [ ] Both files use LF line endings (via `.gitattributes`)

---

## P1.8 — StateStore

**Purpose**: Provide atomic, thread-safe JSON persistence for `apps.json` and
`sessions.json` so the daemon can survive restarts and power-loss without corruption.

**Dependencies**: P1.5, P1.6, P1.10

**Files**:
- `Source/Meadow.Daemon/Services/StateStore.cs`

**Implementation details**:

```csharp
public sealed class StateStore
{
    // One semaphore per state file — prevents concurrent writes to the same file.
    // ConcurrentDictionary so new semaphores are created lazily per path.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly DaemonOptions _options;
    private readonly ILogger<StateStore> _logger;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public StateStore(IOptions<DaemonOptions> options, ILogger<StateStore> logger) { ... }

    public Task<AppsState> LoadAppsAsync(CancellationToken ct = default)
        => LoadAsync<AppsState>(DaemonPaths.AppsStatePath(_options), ct);

    public Task SaveAppsAsync(AppsState state, CancellationToken ct = default)
        => SaveAsync(DaemonPaths.AppsStatePath(_options), state, ct);

    public Task<SessionsState> LoadSessionsAsync(CancellationToken ct = default)
        => LoadAsync<SessionsState>(DaemonPaths.SessionsStatePath(_options), ct);

    public Task SaveSessionsAsync(SessionsState state, CancellationToken ct = default)
        => SaveAsync(DaemonPaths.SessionsStatePath(_options), state, ct);

    private async Task<T> LoadAsync<T>(string path, CancellationToken ct)
        where T : new()
    {
        if (!File.Exists(path)) return new T();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, _json, ct)
                   ?? new T();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "State file {Path} is corrupt; resetting to empty", path);
            return new T();
        }
    }

    private async Task SaveAsync<T>(string path, T state, CancellationToken ct)
    {
        var sem = _locks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
                await JsonSerializer.SerializeAsync(stream, state, _json, ct);
            // File.Move with overwrite=true maps to rename(2) on Linux — atomic
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            sem.Release();
        }
    }
}
```

Register in `Program.cs` as `AddSingleton<StateStore>`.

**Edge cases**:
- `new T()` fallback for missing or corrupt files means `AppsState` and `SessionsState`
  must have parameterless constructors. They do (both are `sealed class` with property
  initialisers).
- `File.Move(tmp, path, overwrite: true)` is `rename(2)` on Linux only if `tmp` and
  `path` are on the same filesystem. They are — both are under `/opt/meadow/state/`.
  If they were on different filesystems, `rename(2)` would fail with `EXDEV`. Do NOT use
  `File.Copy` + `File.Delete` as a fallback — it is not atomic.
- The semaphore prevents concurrent writes to the same file but does NOT provide
  read-your-writes consistency across processes. The daemon is the sole writer; this
  is sufficient.
- `CancellationToken` passed to `WaitAsync` means the semaphore acquisition can be
  cancelled. If cancelled after acquiring the semaphore, the `finally` block still
  releases it correctly.

**Testing requirements**:
- Unit test: `SaveAsync` then `LoadAsync` round-trips data correctly
- Unit test: corrupt JSON file → `LoadAsync` returns empty state, logs warning
- Unit test: missing file → `LoadAsync` returns empty state
- Unit test: concurrent `SaveAsync` calls do not corrupt the file
  (run 100 concurrent saves, verify final file is valid JSON)
- Unit test: power-loss simulation — truncate `.tmp` file mid-write, verify
  original file is intact

**Definition of done**:
- [ ] `StateStore` uses `SemaphoreSlim(1)` per file path
- [ ] Atomic write: write `.tmp` then `File.Move(overwrite: true)`
- [ ] Corrupt JSON silently resets to empty state + logs warning
- [ ] Missing file returns empty state without throwing
- [ ] All tests pass

---

## P1.9 — LogEventChannel

**Purpose**: Provide an in-process broadcast channel that captures structured log events
and fans them out to all active `StreamLogs` gRPC subscribers without blocking the
application's normal log pipeline.

**Dependencies**: P1.4, P1.3 (needs LogEvent proto type)

**Files**:
- `Source/Meadow.Daemon/Services/LogEventChannel.cs`
- `Source/Meadow.Daemon/Services/LogEventLoggerProvider.cs`

**Implementation details**:

`LogEventChannel.cs`:
```csharp
public sealed class LogEventChannel : IAsyncDisposable
{
    // Bounded at 10,000 — if no subscriber is reading, events drop rather than OOM.
    private readonly Channel<LogEvent> _channel =
        Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        });

    public bool TryWrite(LogEvent evt) => _channel.Writer.TryWrite(evt);

    // Each call to Subscribe returns an independent async enumerable.
    // Multiple gRPC stream calls each get their own cursor.
    public IAsyncEnumerable<LogEvent> Subscribe(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        // Drain remaining items so subscribers see a clean end
        await foreach (var _ in _channel.Reader.ReadAllAsync()) { }
    }
}
```

`LogEventLoggerProvider.cs`:
```csharp
[ProviderAlias("LogEventChannel")]
public sealed class LogEventLoggerProvider : ILoggerProvider
{
    private readonly LogEventChannel _channel;
    public LogEventLoggerProvider(LogEventChannel channel) => _channel = channel;

    public ILogger CreateLogger(string categoryName)
        => new LogEventLogger(categoryName, _channel);

    public void Dispose() { }
}

internal sealed class LogEventLogger : ILogger
{
    private readonly string _category;
    private readonly LogEventChannel _channel;

    public LogEventLogger(string category, LogEventChannel channel)
    { _category = category; _channel = channel; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var evt = new LogEvent
        {
            Timestamp = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
            Level     = MapLevel(level),
            Category  = _category,
            Message   = formatter(state, exception)
        };
        if (exception != null)
            evt.Properties["exception"] = exception.ToString();
        _channel.TryWrite(evt);
    }

    private static Meadow.Daemon.Contracts.V1.LogLevel MapLevel(LogLevel l) => l switch
    {
        LogLevel.Trace       => Meadow.Daemon.Contracts.V1.LogLevel.Trace,
        LogLevel.Debug       => Meadow.Daemon.Contracts.V1.LogLevel.Debug,
        LogLevel.Information => Meadow.Daemon.Contracts.V1.LogLevel.Info,
        LogLevel.Warning     => Meadow.Daemon.Contracts.V1.LogLevel.Warn,
        LogLevel.Error       => Meadow.Daemon.Contracts.V1.LogLevel.Error,
        LogLevel.Critical    => Meadow.Daemon.Contracts.V1.LogLevel.Critical,
        _ => Meadow.Daemon.Contracts.V1.LogLevel.Info
    };
}
```

Register in `Program.cs`:
```csharp
builder.Services.AddSingleton<LogEventChannel>();
builder.Logging.AddProvider<LogEventLoggerProvider>();
```

**Edge cases**:
- `DropOldest` on overflow means high-volume log bursts during startup do not stall the
  application. Subscribers may miss old events during burst periods.
- `SingleWriter=false` because multiple threads log concurrently.
- `Subscribe` returns `ReadAllAsync` which blocks until the channel is complete or the
  `CancellationToken` fires. When the gRPC call is cancelled (client disconnects),
  the `CancellationToken` fires and `ReadAllAsync` ends cleanly.
- Do not call `_channel.Writer.Complete()` unless the application is shutting down —
  completing the channel prevents any future writes.
- The `LogEventLogger.BeginScope` returns `null`. Scoped log properties are not
  captured. This is acceptable for the initial implementation.

**Testing requirements**:
- Unit test: `TryWrite` returns true when channel has capacity
- Unit test: after 10,001 writes, oldest event is dropped (not newest)
- Unit test: `Subscribe` yields events written after subscription in order
- Unit test: cancelling the subscription token ends enumeration cleanly
- Unit test: two concurrent subscribers both receive the same events

**Definition of done**:
- [ ] `LogEventChannel` uses bounded channel with `DropOldest`
- [ ] `LogEventLoggerProvider` hooks into `ILoggerFactory`
- [ ] Log level mapping covers all `LogLevel` values
- [ ] `Exception` is serialised into `properties["exception"]`
- [ ] All unit tests pass
- [ ] Registered as singleton in `Program.cs`

---

## P1.10 — Domain Models

**Purpose**: Verify and complete the domain model classes so they correctly represent
the runtime state that `StateStore` persists and gRPC RPCs return.

**Dependencies**: P1.3, P1.4

**Files**:
- `Source/Meadow.Daemon/Models/AppRecord.cs` (already exists — verify/extend)
- `Source/Meadow.Daemon/Models/DebugSessionRecord.cs` (already exists — verify/extend)
- `Source/Meadow.Daemon/Models/DaemonState.cs` (already exists — verify/extend)

**Implementation details**:

`AppRecord.cs` — verify these fields are present:
```csharp
public sealed record AppRecord
{
    public string   Name               { get; init; } = "";
    public string   EntryPoint         { get; init; } = "";  // e.g. "MyApp.dll"
    public string[] StartupArgs        { get; init; } = [];
    public Dictionary<string,string> EnvironmentVariables { get; init; } = new();
    public string?  ActiveVersion      { get; set; }   // ULID of active production version
    public string?  DebugVersion       { get; set; }   // deployment ID of debug slot
    public bool     AutoStart          { get; init; } = false;
    public int?     Pid                { get; set; }   // null when not running
    public DateTimeOffset? LastStartedAt { get; set; }
}
```

`DebugSessionRecord.cs` — verify these fields are present (matches existing file):
```csharp
public sealed record DebugSessionRecord
{
    public string         SessionId       { get; init; } = "";
    public string         AppName         { get; init; } = "";
    public int            VsdbgPid        { get; init; }
    public int            VsdbgPort       { get; init; }
    public int?           AppPid          { get; init; }
    public SessionMode    Mode            { get; init; }
    public SessionState   State           { get; set; } = SessionState.Starting;
    public DateTimeOffset StartedAt       { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastActivityAt  { get; set; } = DateTimeOffset.UtcNow;
    public string         CorrelationId   { get; init; } = "";
}
```

`DaemonState.cs` — verify:
```csharp
public sealed class AppsState
{
    public List<AppRecord> Apps { get; set; } = [];
}

public sealed class SessionsState
{
    public List<DebugSessionRecord> Sessions { get; set; } = [];
}
```

Add `[JsonSerializable]` source-gen attributes to enable trimmer-safe serialisation:
```csharp
// Source/Meadow.Daemon/Models/DaemonJsonContext.cs
[JsonSerializable(typeof(AppsState))]
[JsonSerializable(typeof(SessionsState))]
[JsonSerializable(typeof(AppRecord))]
[JsonSerializable(typeof(DebugSessionRecord))]
internal sealed partial class DaemonJsonContext : JsonSerializerContext { }
```

Pass `DaemonJsonContext.Default` as the `JsonSerializerOptions` source in `StateStore`.

**Edge cases**:
- `AppRecord.Pid` is `int?` — it is `null` when the app is not running and populated
  when running. Never store 0 as "not running"; use `null`.
- `AppRecord.EnvironmentVariables` is a `Dictionary` — JSON round-trip preserves it.
  Values may include secrets; `StateStore` must not log them at Debug level.
- `DebugSessionRecord.State` has a `set` accessor (not `init`) because state transitions
  happen after creation. `LastActivityAt` is similarly mutable.
- `record` types require C# 9+. Both are `sealed record` not `sealed class` — equality
  is structural, which is useful for unit tests.

**Testing requirements**:
- Unit test: `AppRecord` round-trips through `JsonSerializer` with all fields preserved
- Unit test: `DebugSessionRecord` round-trips correctly including enum values
- Unit test: `AppsState` with empty list serialises to `{"apps":[]}` (camelCase)
- Unit test: null `Pid` serialises correctly and deserialises back to `null`

**Definition of done**:
- [ ] All three model files exist with fields matching the spec above
- [ ] `DaemonJsonContext` source-gen context covers all model types
- [ ] JSON round-trip unit tests pass
- [ ] No `set` accessors except on explicitly mutable fields (`State`, `LastActivityAt`, `Pid`)
