# Phase 6 — Provisioning

Implements the automatic first-time setup that runs on every F5 press: SSH connection,
device detection, platform validation, daemon installation, vsdbg installation, and
SSH authentication management.

Task order:
```
P6.7 (SSH Auth)  ──────────────────────────────────▶ P6.5 (Orchestrator)
P6.1 (detect.sh + Detector) ─┐
P6.2 (PlatformValidator)     ├─▶ P6.5 (Orchestrator) ─▶ P5.9 (Debug Launch Provider)
P6.3 (DaemonInstaller)       │
P6.4 (VsdbgInstallClient)    ┘
P6.6 (setup-meadow.sh) — standalone script (no code dependency)
P6.8 (Version Negotiation) ──▶ P6.5
```

---

## P6.1 — detect.sh and CapabilityDetector

**Purpose**: Run a single remote shell command that emits a structured JSON capability
report, then parse it into a typed C# object so all provisioning decisions are made in
managed code on the Windows side.

**Dependencies**: P4.3, P4.5

**Files**:
- `Source/VsExtension/Resources/detect.sh` (shell script — embedded resource)
- `Source/VsExtension/Provisioning/CapabilityDetector.cs`
- `Source/VsExtension/Provisioning/DetectionResult.cs`

**Implementation details**:

`detect.sh` (see full listing in `docs/vsix/10-provisioning-system.md §2`):
- Must produce valid JSON on stdout and nothing else
- All errors written to stderr (not stdout)
- Tolerant of missing tools: each check uses `|| echo ""` or `|| echo false` fallbacks
- Runs in < 5 seconds on all target hardware
- Must handle missing `/etc/os-release` (graceful degradation)

`DetectionResult.cs` — C# model matching the JSON schema:
```csharp
public sealed class DetectionResult
{
    public int    SchemaVersion { get; init; }
    public string Timestamp     { get; init; } = "";
    public DetectionHost       Host       { get; init; } = new();
    public DetectionFilesystem Filesystem { get; init; } = new();
    public DetectionDaemon     Daemon     { get; init; } = new();
    public DetectionVsdbg      Vsdbg      { get; init; } = new();
    public DetectionRuntime    Runtime    { get; init; } = new();
}

public sealed class DetectionHost
{
    [JsonPropertyName("arch")]        public string Arch       { get; init; } = "";
    [JsonPropertyName("kernel")]      public string Kernel     { get; init; } = "";
    [JsonPropertyName("os_id")]       public string OsId       { get; init; } = "";
    [JsonPropertyName("os_version")]  public string OsVersion  { get; init; } = "";
    [JsonPropertyName("os_pretty")]   public string OsPretty   { get; init; } = "";
    [JsonPropertyName("hostname")]    public string Hostname   { get; init; } = "";
    [JsonPropertyName("user")]        public string User       { get; init; } = "";
    [JsonPropertyName("uid")]         public int    Uid        { get; init; }
    [JsonPropertyName("linger")]      public bool   Linger     { get; init; }
}

// DetectionFilesystem, DetectionDaemon, DetectionVsdbg, DetectionRuntime follow same pattern
```

`CapabilityDetector.cs`:
```csharp
public sealed class CapabilityDetector
{
    private static readonly string DetectScript = LoadEmbeddedScript("detect.sh");

    public async Task<DetectionResult> DetectAsync(SshSession session, CancellationToken ct)
    {
        // Upload the script content inline via stdin (avoid file upload for speed)
        var (exitCode, stdout, stderr) = await session.ExecuteAsync(
            $"bash -s <<'DETECT_SCRIPT'\n{DetectScript}\nDETECT_SCRIPT",
            ct);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            throw new ProvisioningException(
                $"Capability detection failed (exit {exitCode}). Stderr: {stderr}");

        try
        {
            var result = JsonSerializer.Deserialize<DetectionResult>(stdout,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return result ?? throw new ProvisioningException("Detection returned null JSON");
        }
        catch (JsonException ex)
        {
            throw new ProvisioningException(
                $"Detection returned invalid JSON: {ex.Message}\nOutput was: {stdout[..Math.Min(200, stdout.Length)]}");
        }
    }

    private static string LoadEmbeddedScript(string name)
    {
        var asm    = Assembly.GetExecutingAssembly();
        var resName = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

**Edge cases**:
- Passing the script via stdin heredoc avoids a temporary file upload, but requires the
  heredoc delimiter (`DETECT_SCRIPT`) to not appear in the script body. Verify this.
- SSH.NET command execution has a default timeout. `detect.sh` should complete in < 5s;
  set a 10s timeout on `ExecuteAsync` for the detection command specifically.
- The script emits JSON. If bash echoes a login banner before the JSON (common on
  misconfigured Pi setups), JSON parsing fails. Strip any non-JSON prefix from stdout:
  find the first `{` character and parse from there.
- `SchemaVersion` field allows future detect.sh versions to add fields without breaking
  older VSIX. Always check `SchemaVersion >= 1`.

**Testing requirements**:
- Unit test: valid JSON from detect.sh parses into `DetectionResult` with all fields
- Unit test: non-JSON stdout (banner + JSON) → strip banner, parse successfully
- Unit test: empty stdout → `ProvisioningException`
- Unit test: invalid JSON → `ProvisioningException` with truncated output
- Integration test: run detect.sh on a real Pi, verify all fields are populated

**Definition of done**:
- [x] `detect.sh` embedded in VSIX as a resource
- [x] Script executed via stdin heredoc (no file upload)
- [x] JSON prefix stripping (handles login banners)
- [ ] 10-second timeout on detection command (uses SshSession default 30s)
- [x] All field values from the JSON schema are populated in `DetectionResult`
- [ ] All unit tests pass

---

## P6.2 — PlatformValidator

**Purpose**: Evaluate a `DetectionResult` against the supported platform requirements
and produce a structured report listing which checks passed, which failed (fatal),
and which are warnings.

**Dependencies**: P6.1

**Files**:
- `Source/VsExtension/Provisioning/PlatformValidator.cs`
- `Source/VsExtension/Provisioning/ValidationReport.cs`

**Implementation details**:

```csharp
public sealed class ValidationItem
{
    public string Check   { get; init; } = "";
    public bool   Passed  { get; init; }
    public bool   IsFatal { get; init; }
    public string Message { get; init; } = "";
}

public sealed class ValidationReport
{
    public IReadOnlyList<ValidationItem> Items     { get; init; } = [];
    public bool AllFatalsPassed => Items.All(i => !i.IsFatal || i.Passed);
    public IReadOnlyList<ValidationItem> Failures
        => Items.Where(i => !i.Passed && i.IsFatal).ToList();
    public IReadOnlyList<ValidationItem> Warnings
        => Items.Where(i => !i.Passed && !i.IsFatal).ToList();
}

public sealed class PlatformValidator
{
    public ValidationReport Validate(DetectionResult result)
    {
        var items = new List<ValidationItem>();

        // Fatal checks (block provisioning)
        items.Add(Check(
            "Architecture",
            result.Host.Arch == "aarch64",
            fatal: true,
            passed: $"ARM64 ({result.Host.Arch})",
            failed: $"Unsupported architecture '{result.Host.Arch}'. " +
                    "Only ARM64 (aarch64) is supported."));

        items.Add(Check(
            "Operating System",
            result.Host.OsId is "raspbian" or "debian" or "ubuntu",
            fatal: true,
            passed: result.Host.OsPretty,
            failed: $"Unsupported OS '{result.Host.OsPretty}'. " +
                    "Supported: Raspberry Pi OS 64-bit (Bookworm), Debian 12."));

        items.Add(Check(
            "OS Version",
            int.TryParse(result.Host.OsVersion, out var ver) && ver >= 12,
            fatal: true,
            passed: $"Version {result.Host.OsVersion} (OK)",
            failed: $"OS version {result.Host.OsVersion} is too old. Minimum: 12 (Bookworm)."));

        items.Add(Check(
            "systemd User Session",
            result.Runtime.SystemdUserAvailable,
            fatal: true,
            passed: "systemd --user available",
            failed: "systemd user session not available. Ensure pam_systemd.so is configured."));

        items.Add(Check(
            "Directory /opt/meadow",
            result.Filesystem.OptMeadowExists,
            fatal: true,
            passed: "/opt/meadow exists",
            failed: "/opt/meadow does not exist. Run setup-meadow.sh on the device first:\n" +
                    "  curl -sSL .../setup-meadow.sh | sudo bash"));

        items.Add(Check(
            "/opt/meadow Writable",
            result.Filesystem.OptMeadowWritable,
            fatal: true,
            passed: "/opt/meadow is writable",
            failed: "/opt/meadow is not writable. Run setup-meadow.sh to fix ownership."));

        items.Add(Check(
            "Disk Space",
            result.Filesystem.FreeBytesOpt >= 200L * 1024 * 1024,
            fatal: true,
            passed: $"{result.Filesystem.FreeBytesOpt / 1024 / 1024} MB free (OK)",
            failed: $"Insufficient disk space: {result.Filesystem.FreeBytesOpt / 1024 / 1024} MB " +
                    "available, 200 MB required."));

        // Warning checks (advisory only)
        items.Add(Check(
            "Linger Enabled",
            result.Host.Linger,
            fatal: false,
            passed: "Linger enabled (service persists after logout)",
            failed: "Linger not enabled. The daemon will stop when you disconnect from SSH. " +
                    "Run: loginctl enable-linger $USER"));

        items.Add(Check(
            "curl Available",
            result.Runtime.CurlAvailable,
            fatal: false,
            passed: "curl available (online vsdbg install supported)",
            failed: "curl not found. vsdbg will be installed from bundled offline tarball."));

        return new ValidationReport { Items = items };
    }

    private static ValidationItem Check(
        string name, bool condition, bool fatal, string passed, string failed)
        => new()
        {
            Check   = name,
            Passed  = condition,
            IsFatal = fatal,
            Message = condition ? passed : failed
        };
}
```

**Edge cases**:
- An OS with `OsId = "ubuntu"` should require `OsVersion >= "22"` (Ubuntu 22.04).
  The current check uses `>= 12` (Debian version scheme). Ubuntu uses a different
  version number. Add a separate branch:
  ```csharp
  bool osVersionOk = result.Host.OsId == "ubuntu"
      ? int.TryParse(result.Host.OsVersion.Split('.')[0], out var uv) && uv >= 22
      : int.TryParse(result.Host.OsVersion, out var dv) && dv >= 12;
  ```
- `OptMeadowExists` but not `OptMeadowWritable` is a fatal failure that requires
  `sudo` to fix. Distinguish these two failures clearly in the error message.
- Validation is pure (no I/O). It can be unit tested without any network connection.

**Testing requirements**:
- Unit test: ARM64 Debian 12 → all fatal checks pass
- Unit test: `arch = armv7l` → fails architecture check (fatal)
- Unit test: `os_version = "11"` → fails OS version check (fatal)
- Unit test: `opt_meadow_exists = false` → fails /opt/meadow check (fatal)
- Unit test: `linger = false` → warning (non-fatal)
- Unit test: `AllFatalsPassed` is true when only warnings failed

**Definition of done**:
- [x] All 7 fatal checks listed above implemented
- [x] All 2 advisory warnings listed above implemented
- [x] Ubuntu version handled separately from Debian version
- [x] `ValidationReport.AllFatalsPassed` correctly computed
- [x] Error messages include actionable instructions
- [ ] All unit tests pass

---

## P6.3 — DaemonInstaller (VSIX)

**Purpose**: Install or upgrade the daemon binary, systemd service file, and configuration
on the target device via SFTP and SSH commands, using atomic upload patterns.

**Dependencies**: P4.3, P6.1

**Files**:
- `Source/VsExtension/Provisioning/DaemonInstaller.cs`
- `Source/VsExtension/Resources/meadow-daemon` (embedded binary)
- `Source/VsExtension/Resources/meadow-daemon.service.template` (embedded template)

**Implementation details**:

```csharp
public enum DaemonInstallAction { None, Install, Upgrade, Reinstall }

public sealed class DaemonInstaller
{
    // Embedded version and hash are stamped at VSIX build time
    public static readonly string RequiredVersion = ThisAssembly.DaemonVersion;
    public static readonly string RequiredSha256  = ThisAssembly.DaemonSha256;

    public DaemonInstallAction DetermineAction(DetectionResult detection)
    {
        if (!detection.Daemon.BinaryExists)         return DaemonInstallAction.Install;
        if (detection.Daemon.BinaryVersion == "")   return DaemonInstallAction.Reinstall;

        if (!string.IsNullOrEmpty(detection.Daemon.BinarySha256)
            && detection.Daemon.BinarySha256 != RequiredSha256)
            return DaemonInstallAction.Reinstall;

        if (IsVersionLessThan(detection.Daemon.BinaryVersion, RequiredVersion))
            return DaemonInstallAction.Upgrade;

        return DaemonInstallAction.None;
    }

    public async Task InstallAsync(
        SshSession session,
        DaemonInstallAction action,
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (action == DaemonInstallAction.None) return;

        progress.Report($"Installing daemon ({action})...");

        // If upgrading: backup existing binary
        if (action == DaemonInstallAction.Upgrade)
        {
            progress.Report("Backing up existing daemon binary...");
            await session.ExecuteAsync(
                "cp /opt/meadow/bin/meadow-daemon /opt/meadow/bin/meadow-daemon.bak 2>/dev/null; true",
                ct);
        }

        // Upload new binary atomically
        progress.Report($"Uploading meadow-daemon ({GetBinarySize() / 1024 / 1024} MB)...");
        await using var binaryStream = GetEmbeddedBinary();
        await session.UploadFileAsync(binaryStream, "/opt/meadow/bin/meadow-daemon.new",
            new Progress<long>(bytes =>
                progress.Report($"  {bytes / 1024 / 1024} MB uploaded...")),
            ct);

        var (rc, _, err) = await session.ExecuteAsync(
            "chmod 755 /opt/meadow/bin/meadow-daemon.new && " +
            "mv /opt/meadow/bin/meadow-daemon.new /opt/meadow/bin/meadow-daemon",
            ct);
        if (rc != 0)
            throw new ProvisioningException($"Failed to install daemon binary: {err}");

        // Upload service file
        progress.Report("Installing systemd service...");
        var serviceContent = RenderServiceTemplate();
        await session.UploadTextAsync(serviceContent,
            "$HOME/.config/systemd/user/meadow-daemon.service", ct);

        // Upload default config
        await session.UploadTextAsync(DefaultDaemonConf,
            "/etc/meadow/daemon.conf", ct);

        // Enable and start
        progress.Report("Enabling and starting service...");
        var startCmd = action == DaemonInstallAction.Upgrade
            ? "systemctl --user daemon-reload && systemctl --user restart meadow-daemon"
            : "systemctl --user daemon-reload && systemctl --user enable meadow-daemon && systemctl --user start meadow-daemon";

        (rc, _, err) = await session.ExecuteAsync(startCmd, ct);
        if (rc != 0)
            throw new ProvisioningException($"Failed to start service: {err}");

        progress.Report("Daemon installed and running.");
    }

    public async Task<bool> WaitForHealthAsync(GrpcChannel channel,
        TimeSpan timeout, CancellationToken ct)
    {
        var client   = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await client.PingAsync(new PingRequest(), cancellationToken: ct);
                return true;
            }
            catch (RpcException) { /* not ready yet */ }
            await Task.Delay(2000, ct);
        }
        return false;
    }

    private string RenderServiceTemplate()
    {
        var template = LoadEmbeddedText("meadow-daemon.service.template");
        return template
            .Replace("@@DAEMON_BIN@@", "/opt/meadow/bin/meadow-daemon")
            .Replace("@@INSTALL_DIR@@", "/opt/meadow")
            .Replace("@@EXTRA_ARGS@@", "");
    }
}
```

Note: `ThisAssembly.DaemonVersion` and `ThisAssembly.DaemonSha256` are T4 or source
generator constants written at VSIX build time from the bundled binary.

**Edge cases**:
- The daemon binary is embedded in the VSIX (~35 MB). This significantly increases
  VSIX file size. Accept this — the VSIX is a developer tool, not a user-facing app.
- `mv .new → daemon` is atomic. But if `mkdir /opt/meadow/bin` has not been run (no
  host bootstrap), the upload fails. The `PlatformValidator` checks for `/opt/meadow`
  existence and writability before `DaemonInstaller` runs.
- Service file path `$HOME/.config/systemd/user/` — the `$HOME` must be expanded
  server-side. Pass the full path with `$HOME` in the SSH command, not in SFTP.
  Better: use `UploadTextAsync` with the resolved path from `detection.Host.User`.
- `/etc/meadow/daemon.conf` upload requires the directory to exist (created by
  setup-meadow.sh). If it doesn't exist, the SFTP upload will fail — check with
  `EnsureRemoteDir`.

**Testing requirements**:
- Unit test: `DetermineAction` returns `Install` when binary absent
- Unit test: `DetermineAction` returns `Upgrade` when version < required
- Unit test: `DetermineAction` returns `Reinstall` when SHA-256 mismatch
- Unit test: `DetermineAction` returns `None` when version matches and SHA-256 matches
- Integration test: install on a clean Pi, verify service is running
- Integration test: upgrade — old binary backed up, new binary running
- Integration test: `WaitForHealthAsync` returns true within 30s of service start

**Definition of done**:
- [x] `DetermineAction` correctly classifies Install/Upgrade/Reinstall/None
- [x] Binary uploaded as `.new` then atomically renamed
- [x] Old binary backed up on Upgrade
- [x] Service file rendered from template with `@@PLACEHOLDERS@@` substituted
- [x] `systemctl enable && start` (install) vs `restart` (upgrade)
- [x] `WaitForHealthAsync` polls Ping every 2s
- [ ] All tests pass

---

## P6.4 — VsdbgInstallClient (VSIX)

**Purpose**: Determine whether vsdbg needs installation on the target device and
orchestrate installation via the daemon's gRPC `InstallVsdbg` or `UploadVsdbgTarball`
RPCs, with an offline tarball fallback.

**Dependencies**: P4.6, P5.8, P6.1

**Files**:
- `Source/VsExtension/Provisioning/VsdbgInstallClient.cs`

**Implementation details**:

```csharp
public sealed class VsdbgInstallClient
{
    public static readonly string RequiredVsdbgMin = "17.0.0";
    public static readonly string PreferredVsdbg   = "17.12.11230";

    public bool NeedsInstall(DetectionResult detection)
    {
        if (!detection.Vsdbg.BinaryExists) return true;
        var installed = detection.Vsdbg.Version;
        if (string.IsNullOrEmpty(installed)) return true;
        return IsVersionLessThan(installed, RequiredVsdbgMin);
    }

    public async Task InstallAsync(
        GrpcChannel channel,
        bool curlAvailable,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);

        if (curlAvailable)
        {
            await TryOnlineInstallAsync(client, progress, ct);
        }
        else
        {
            progress.Report("curl not available; using offline tarball...");
            await OfflineTarballInstallAsync(client, progress, ct);
        }
    }

    private async Task TryOnlineInstallAsync(
        MeadowDaemonService.MeadowDaemonServiceClient client,
        IProgress<string> progress, CancellationToken ct)
    {
        try
        {
            using var call = client.InstallVsdbg(
                new InstallVsdbgRequest { Version = PreferredVsdbg },
                cancellationToken: ct);

            await foreach (var msg in call.ResponseStream.ReadAllAsync(ct))
            {
                progress.Report(msg.Message);
                if (msg.Done) return;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Internal)
        {
            progress.Report($"Online install failed: {ex.Status.Detail}. Falling back to tarball.");
            await OfflineTarballInstallAsync(client, progress, ct);
        }
    }

    private async Task OfflineTarballInstallAsync(
        MeadowDaemonService.MeadowDaemonServiceClient client,
        IProgress<string> progress, CancellationToken ct)
    {
        var tarball = GetEmbeddedTarball();
        if (tarball is null)
        {
            throw new ProvisioningException(
                "vsdbg offline tarball not bundled in this VSIX. " +
                "Ensure the device has internet access for online installation.");
        }

        progress.Report($"Uploading vsdbg tarball ({tarball.Length / 1024 / 1024} MB)...");

        using var call = client.UploadVsdbgTarball(cancellationToken: ct);
        const int chunkSize = 256 * 1024;  // 256 KB chunks
        var buffer = new byte[chunkSize];
        int bytesRead;
        while ((bytesRead = await tarball.ReadAsync(buffer, 0, chunkSize, ct)) > 0)
        {
            await call.RequestStream.WriteAsync(new UploadVsdbgTarballRequest
            {
                Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead),
            }, ct);
        }
        // Send SHA-256 in the last chunk
        await call.RequestStream.WriteAsync(new UploadVsdbgTarballRequest
        {
            Sha256 = GetEmbeddedTarballSha256(),
        }, ct);
        await call.RequestStream.CompleteAsync();
        var response = await call;
        if (!response.Success)
            throw new ProvisioningException("vsdbg tarball installation failed.");

        progress.Report("vsdbg installed successfully.");
    }

    private static Stream? GetEmbeddedTarball()
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains("vsdbg-linux-arm64.tar.gz"));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }
}
```

**Edge cases**:
- The embedded tarball is optional (build condition `Exists('Resources\vsdbg-linux-arm64.tar.gz')`).
  When absent, `GetEmbeddedTarball()` returns null. If online install also fails,
  surface a clear error telling the user to ensure internet access.
- `UploadVsdbgTarball` client-streaming: the `SHA-256` field is sent in a separate final
  chunk after the data. The daemon accumulates all `Data` bytes, then verifies SHA-256
  when it sees the final chunk with `HasSha256`. This requires the proto to use `optional`
  for both `data` and `sha256` fields in `UploadVsdbgTarballRequest`.
- The server-streaming `InstallVsdbg` RPC returns a `Done = true` message on completion.
  Handle early completion (daemon returns `Done` before stream closes).

**Testing requirements**:
- Unit test: `NeedsInstall` returns true when binary absent
- Unit test: `NeedsInstall` returns false when version >= required minimum
- Unit test: `NeedsInstall` returns true when version < required minimum
- Integration test: online install streams progress messages and completes
- Integration test: offline install with embedded tarball completes successfully
- Integration test: online install failure falls back to tarball

**Definition of done**:
- [x] `NeedsInstall` checks binary existence and version
- [x] Online install via `InstallVsdbg` streaming RPC
- [x] Offline fallback via `UploadVsdbgTarball` — uses SFTP then unary RPC (matches actual proto)
- [x] Embedded tarball absence → clear error when online also unavailable
- [x] SHA-256 sent with tarball path in unary request
- [ ] All tests pass

---

## P6.5 — ProvisioningOrchestrator

**Purpose**: Orchestrate the complete provisioning state machine — detect, validate,
install daemon, wait for health, install vsdbg — caching the result to skip on
subsequent F5 presses when nothing has changed.

**Dependencies**: P6.1, P6.2, P6.3, P6.4, P6.7, P6.8, P4.5, P4.6

**Files**:
- `Source/VsExtension/Provisioning/ProvisioningOrchestrator.cs`
- `Source/VsExtension/Provisioning/ProvisioningResult.cs`

**Implementation details**:

```csharp
public sealed class ProvisioningStep
{
    public string Name    { get; init; } = "";
    public bool   Skipped { get; init; }
    public bool   Success { get; init; }
    public string Message { get; init; } = "";
}

public sealed class ProvisioningResult
{
    public bool                          Success   { get; init; }
    public IReadOnlyList<ProvisioningStep> Steps   { get; init; } = [];
    public string?                       Error     { get; init; }
    // The gRPC channel is returned on success so the caller can proceed
    public GrpcChannel?                  Channel   { get; init; }
}

public sealed class ProvisioningOrchestrator
{
    // Cache: if the daemon is already provisioned and healthy, skip
    private static readonly ConcurrentDictionary<string, ProvisioningCache> _cache = new();

    private sealed record ProvisioningCache(
        string DaemonVersion, string VsdbgVersion, DateTimeOffset At);

    public async Task<ProvisioningResult> ProvisionAsync(
        SshSession session,
        IOutputWindowService output,
        CancellationToken ct)
    {
        var steps = new List<ProvisioningStep>();
        output.Activate(OutputPane.Provisioning);

        // --- Step 1: Capability Detection ---
        output.WriteLine(OutputPane.Provisioning, "[1/7] Detecting device capabilities...");
        DetectionResult detection;
        try
        {
            detection = await new CapabilityDetector().DetectAsync(session, ct);
            steps.Add(Step("Detection", true, $"Host: {detection.Host.OsPretty} {detection.Host.Arch}"));
        }
        catch (ProvisioningException ex)
        {
            return Fail(steps, "Detection", ex.Message);
        }

        // --- Step 2: Platform Validation ---
        output.WriteLine(OutputPane.Provisioning, "[2/7] Validating platform...");
        var validator = new PlatformValidator();
        var report    = validator.Validate(detection);

        foreach (var item in report.Items)
            output.WriteLine(OutputPane.Provisioning,
                $"  [{(item.Passed ? "OK" : item.IsFatal ? "FAIL" : "WARN")}] {item.Check}: {item.Message}");

        if (!report.AllFatalsPassed)
            return Fail(steps, "Platform validation",
                $"Platform check failed: {string.Join(", ", report.Failures.Select(f => f.Check))}");

        steps.Add(Step("Platform validation", true, $"{report.Warnings.Count} warnings"));

        // --- Step 3: Check daemon ---
        output.WriteLine(OutputPane.Provisioning, "[3/7] Checking daemon...");
        var installer = new DaemonInstaller();
        var action    = installer.DetermineAction(detection);

        if (action == DaemonInstallAction.None)
        {
            output.WriteLine(OutputPane.Provisioning,
                $"  Daemon {detection.Daemon.BinaryVersion} is current (no action needed)");
            steps.Add(Step("Daemon", true, "up to date", skipped: true));
        }
        else
        {
            output.WriteLine(OutputPane.Provisioning, $"  Action: {action}");
            var progress = new Progress<string>(
                msg => output.WriteLine(OutputPane.Provisioning, $"  {msg}"));
            await installer.InstallAsync(session, action, progress, ct);
            steps.Add(Step("Daemon install", true, action.ToString()));
        }

        // --- Step 4: Open gRPC channel ---
        output.WriteLine(OutputPane.Provisioning, "[4/7] Connecting to daemon...");
        var factory = new GrpcChannelFactory();
        var channel = await factory.GetOrCreateChannelAsync(session, ct);

        // --- Step 5: Wait for health ---
        output.WriteLine(OutputPane.Provisioning, "[5/7] Waiting for daemon health...");
        var healthy = await installer.WaitForHealthAsync(channel,
            timeout: TimeSpan.FromSeconds(30), ct);
        if (!healthy)
            return Fail(steps, "Daemon health", "Daemon did not become healthy within 30s");
        steps.Add(Step("Daemon health", true, "OK"));

        // --- Step 6: Version negotiation ---
        output.WriteLine(OutputPane.Provisioning, "[6/7] Negotiating protocol version...");
        var negotiator = new VersionNegotiator();
        var nego = await negotiator.NegotiateAsync(channel, ct);
        if (!nego.Compatible)
            return Fail(steps, "Version negotiation", nego.Error ?? "Protocol incompatible");
        steps.Add(Step("Version negotiation", true, $"proto v{nego.ProtoVersion}"));

        // --- Step 7: vsdbg ---
        output.WriteLine(OutputPane.Provisioning, "[7/7] Checking vsdbg...");
        var vsdbgClient = new VsdbgInstallClient();
        if (vsdbgClient.NeedsInstall(detection))
        {
            var vsdbgProg = new Progress<string>(
                msg => output.WriteLine(OutputPane.Provisioning, $"  {msg}"));
            await vsdbgClient.InstallAsync(channel, detection.Runtime.CurlAvailable,
                vsdbgProg, ct);
            steps.Add(Step("vsdbg install", true, "installed"));
        }
        else
        {
            output.WriteLine(OutputPane.Provisioning,
                $"  vsdbg {detection.Vsdbg.Version} is current");
            steps.Add(Step("vsdbg", true, "up to date", skipped: true));
        }

        // Cache result
        _cache[session.Host] = new ProvisioningCache(
            detection.Daemon.BinaryVersion, detection.Vsdbg.Version, DateTimeOffset.UtcNow);

        output.WriteLine(OutputPane.Provisioning,
            $"Provisioning complete ({steps.Count(s => !s.Skipped)} steps executed)");

        return new ProvisioningResult { Success = true, Steps = steps, Channel = channel };
    }

    private static ProvisioningResult Fail(
        List<ProvisioningStep> steps, string step, string error)
    {
        steps.Add(new ProvisioningStep { Name = step, Success = false, Message = error });
        return new ProvisioningResult { Success = false, Steps = steps, Error = error };
    }

    private static ProvisioningStep Step(
        string name, bool success, string message, bool skipped = false)
        => new() { Name = name, Success = success, Message = message, Skipped = skipped };
}
```

**Edge cases**:
- The cache is keyed by `session.Host`. If the user changes the daemon version on the
  Pi manually, the cache is stale. Invalidate the cache when:
  - The detected daemon version differs from the cached version
  - The cache is > 10 minutes old
  - The user explicitly runs `PiDbg: Repair Connection`
- Provisioning is called from the VS UI thread context via `QueryDebugTargetsAsync`.
  All async operations must be awaitable and all sync-over-async patterns avoided.
- If provisioning fails mid-way (e.g. daemon installed but health check fails),
  the next F5 press re-runs from the beginning (detection step), which correctly
  identifies the partial state and resumes.

**Testing requirements**:
- Integration test: first F5 on a clean Pi — full provisioning succeeds
- Integration test: second F5 — all steps skipped (cache hit)
- Integration test: `Repair` command — cache cleared, full provisioning re-runs
- Unit test: fatal platform validation failure → `ProvisioningResult.Success = false`
  with `Error` message
- Unit test: version negotiation failure → correct error surfaced

**Definition of done**:
- [x] 7-step orchestration: detect, validate, daemon install, channel, health, negotiate, vsdbg
- [x] Each step logged to `Provisioning` output pane
- [x] Cache invalidation on version mismatch or age > 10m
- [x] `ProvisioningResult.Channel` returned on success for use by deploy step
- [ ] All integration tests pass

---

## P6.6 — setup-meadow.sh Host Bootstrap Script

**Purpose**: Create the one-time shell script that a developer runs once with `sudo` to
prepare the device directory skeleton and enable linger before VSIX provisioning begins.

**Dependencies**: None (standalone script)

**Files**:
- `scripts/setup-meadow.sh`

**Implementation details**:

```bash
#!/usr/bin/env bash
# setup-meadow.sh — One-time device preparation for PiDbg
# Usage: curl -sSL .../setup-meadow.sh | sudo bash
#    or: sudo bash setup-meadow.sh
# Idempotent: safe to run multiple times.
set -euo pipefail

# ── Architecture check ─────────────────────────────────────────────────────
ARCH=$(uname -m)
if [ "$ARCH" != "aarch64" ]; then
  echo "ERROR: ARM64 (aarch64) required. Detected: $ARCH" >&2
  exit 1
fi

# ── Identify target user ───────────────────────────────────────────────────
TARGET_USER="${SUDO_USER:-$(logname 2>/dev/null || id -un)}"
TARGET_GID=$(id -g "$TARGET_USER")
echo "==> Preparing device for user: $TARGET_USER"

# ── Create directory skeleton ──────────────────────────────────────────────
install -d -m 755 -o "$TARGET_USER" -g "$TARGET_USER" \
  /opt/meadow \
  /opt/meadow/bin \
  /opt/meadow/vsdbg \
  /opt/meadow/apps \
  /opt/meadow/logs

install -d -m 700 -o "$TARGET_USER" -g "$TARGET_USER" \
  /opt/meadow/state

# /etc/meadow: root-owned, group-readable by target user
install -d -m 750 /etc/meadow
chown "root:$TARGET_GID" /etc/meadow

echo "  Created: /opt/meadow/ (owner: $TARGET_USER)"
echo "  Created: /etc/meadow/ (group: $TARGET_GID)"

# ── Enable linger ──────────────────────────────────────────────────────────
if loginctl show-user "$TARGET_USER" --property=Linger 2>/dev/null | grep -q "yes"; then
  echo "  Linger already enabled for $TARGET_USER"
else
  loginctl enable-linger "$TARGET_USER"
  echo "  Linger enabled for $TARGET_USER"
fi

# ── Ensure systemd user service directory ──────────────────────────────────
USER_HOME=$(eval echo "~$TARGET_USER")
SERVICE_DIR="$USER_HOME/.config/systemd/user"
if [ ! -d "$SERVICE_DIR" ]; then
  install -d -m 755 -o "$TARGET_USER" -g "$TARGET_GID" "$SERVICE_DIR"
  echo "  Created: $SERVICE_DIR"
fi

# ── Ensure user XDG runtime dir is configured ─────────────────────────────
# On headless systems pam_systemd may not run — DBUS_SESSION_BUS_ADDRESS
# can be missing. This is advisory; we don't fail here.
if ! systemctl --user --machine="${TARGET_USER}@.host" is-system-running &>/dev/null; then
  echo "  NOTE: systemd user session not active yet."
  echo "        Reboot or re-login as $TARGET_USER before using PiDbg."
fi

echo ""
echo "==> Host bootstrap complete!"
echo "    Connect Visual Studio to this device and press F5 to finish provisioning."
```

**Edge cases**:
- `SUDO_USER` is set when running `sudo bash setup-meadow.sh`. It identifies the
  invoking user, not root. `logname` is the fallback for `curl ... | sudo bash`.
- `install -d` is idempotent (no-op if directory exists). Prefer over `mkdir -p`.
- `/opt/meadow/state/` has mode `700` (no group/world access) because it contains
  session records that may include PID information.
- If run multiple times, linger re-enable is suppressed to avoid noise.
- The script must not install or start the daemon — that is the VSIX's job.
- Line endings: this file must have LF endings (enforced by `.gitattributes`).

**Testing requirements**:
- Test on Raspberry Pi OS 64-bit: script runs without errors
- Test: run twice → idempotent (no errors on second run)
- Test: verify `/opt/meadow/` ownership after run (`ls -la /opt/meadow`)
- Test: verify linger enabled (`loginctl show-user pi --property=Linger`)
- Test: verify `/opt/meadow/state` has mode 700

**Definition of done**:
- [x] Script uses `set -euo pipefail`
- [x] Architecture check (aarch64 only)
- [x] `TARGET_USER` correctly identified from `SUDO_USER` or `logname`
- [x] All directories created with correct ownership and permissions
- [x] Linger enabled for target user
- [x] Script is idempotent (safe to run multiple times)
- [x] File has LF line endings (enforced by .gitattributes)

---

## P6.7 — SSH Authentication Manager

**Purpose**: Manage SSH credentials for each target device — storing passwords in the VS
credential store, generating and installing SSH keys on first connect, and verifying
host fingerprints.

**Dependencies**: P4.3

**Files**:
- `Source/VsExtension/Infrastructure/SshAuthManager.cs`
- `Source/VsExtension/UI/ConnectDialog.xaml` + `.cs`

**Implementation details**:

```csharp
public sealed class SshAuthManager
{
    private static readonly string KeyDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiDbg", "ssh");

    public async Task<SshConnectionConfig> GetOrPromptAsync(
        string host, string? defaultUser, AsyncPackage package, CancellationToken ct)
    {
        // Check VS credential store
        var stored = await LoadStoredConfigAsync(host, package);
        if (stored is not null) return stored;

        // Show connect dialog on UI thread
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
        var dialog = new ConnectDialog { Host = host, User = defaultUser ?? "pi" };
        if (dialog.ShowDialog() != true)
            throw new OperationCanceledException("User cancelled connection dialog");

        var config = new SshConnectionConfig(
            Host: dialog.Host, User: dialog.User, Port: dialog.Port,
            Password: dialog.Password, KeyFile: null);

        // Store credentials for next time
        await StoreConfigAsync(host, config, package);
        return config;
    }

    public async Task InstallSshKeyIfNeededAsync(
        SshSession session, CancellationToken ct)
    {
        var pubKey = await EnsureKeyPairAsync();

        // Check if key is already in authorized_keys
        var (_, stdout, _) = await session.ExecuteAsync(
            $"grep -qF '{pubKey.Fingerprint}' ~/.ssh/authorized_keys 2>/dev/null && echo YES || echo NO",
            ct);

        if (stdout.Trim() == "YES") return;  // already installed

        // Append to authorized_keys
        var (rc, _, err) = await session.ExecuteAsync(
            $"mkdir -p ~/.ssh && chmod 700 ~/.ssh && " +
            $"echo '{pubKey.PublicKey}' >> ~/.ssh/authorized_keys && " +
            $"chmod 600 ~/.ssh/authorized_keys",
            ct);

        if (rc != 0)
            throw new ProvisioningException($"Failed to install SSH key: {err}");
    }

    public async Task<SshKeyPair> EnsureKeyPairAsync()
    {
        Directory.CreateDirectory(KeyDir);
        var privPath = Path.Combine(KeyDir, "id_pidbg");
        var pubPath  = Path.Combine(KeyDir, "id_pidbg.pub");

        if (File.Exists(privPath) && File.Exists(pubPath))
            return new SshKeyPair(privPath, File.ReadAllText(pubPath));

        // Generate RSA 4096 via SSH.NET
        using var key = new RsaKey();  // SSH.NET generates RSA key
        // Write private key (PKCS#8 PEM format)
        using (var privWriter = new StreamWriter(privPath))
            key.WritePrivateKey(privWriter);
        // Write public key (OpenSSH authorized_keys format)
        var pubKey = $"ssh-rsa {key.GetPublicKey()} pidbg@vsix";
        File.WriteAllText(pubPath, pubKey);
        // Restrict permissions (Windows ACL)
        RestrictFileToCurrentUser(privPath);

        return new SshKeyPair(privPath, pubKey);
    }

    private static void RestrictFileToCurrentUser(string path)
    {
        var info     = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(true, false);  // remove inherited rules
        var rule = new FileSystemAccessRule(
            WindowsIdentity.GetCurrent().Name,
            FileSystemRights.FullControl,
            AccessControlType.Allow);
        security.AddAccessRule(rule);
        info.SetAccessControl(security);
    }
}
```

`ConnectDialog.xaml` — WPF window with:
- Host text field (pre-filled with project `PiDbgHost`)
- User text field (default "pi")
- Port number field (default 22)
- Password box
- "Remember credentials" checkbox
- "Connect" and "Cancel" buttons

Known hosts verification:
```csharp
public bool VerifyHostKey(string host, string fingerprint)
{
    var knownHostsPath = Path.Combine(KeyDir, "known_hosts");
    // Load known_hosts; check if host+fingerprint match
    // If new host: prompt user to verify, then add
    // If mismatch: warn of potential MITM, block connection
}
```

**Edge cases**:
- `RsaKey` generation in SSH.NET: use `SshNet.Security.Cryptography.RsaKey` constructor
  with bit size 4096. The exact API depends on the SSH.NET version.
- `RestrictFileToCurrentUser` is Windows-specific (`System.Security.AccessControl`).
  Guard with `RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`.
- The `ConnectDialog` must run on the UI thread and block until the user responds.
  `ShowDialog()` is modal and blocks appropriately.
- VS credential store: use `IVsPasswordManager` (`SVsPasswordManager` service) to
  store/retrieve the password in the VS encrypted credential store.

**Testing requirements**:
- Unit test: `EnsureKeyPairAsync` creates key files when absent
- Unit test: `EnsureKeyPairAsync` returns existing keys without regenerating
- Integration test: `InstallSshKeyIfNeededAsync` installs key into `authorized_keys`
- Integration test: second call is a no-op (key already installed)
- Manual test: `ConnectDialog` appears, user enters password, connection succeeds

**Definition of done**:
- [x] `EnsureKeyPairAsync` generates ed25519 key pair in `%LOCALAPPDATA%\PiDbg\ssh\` via ssh-keygen
- [x] Private key restricted to current Windows user (ACL)
- [x] `InstallPublicKeyAsync` appends public key to `~/.ssh/authorized_keys`
- [x] Known hosts file written to `%LOCALAPPDATA%\PiDbg\ssh\known_hosts`
- [ ] Credential store integration (VS `IVsPasswordManager`) — using JSON profile file instead
- [x] `ConnectDialog` XAML created and functional

---

## P6.8 — Version Negotiation

**Purpose**: After the daemon is running, verify that the installed daemon proto version
is compatible with the VSIX and surface actionable upgrade instructions when it is not.

**Dependencies**: P4.6, P2.4

**Files**:
- `Source/VsExtension/Provisioning/VersionNegotiator.cs`
- `Source/VsExtension/Provisioning/VersionManifest.cs`

**Implementation details**:

```csharp
public sealed class VersionManifest
{
    public string VsixVersion        { get; init; } = "";
    public string RequiredDaemonMin  { get; init; } = "";
    public string RequiredDaemonMax  { get; init; } = "";  // "1.x.x" wildcard
    public string PreferredDaemon    { get; init; } = "";
    public string RequiredVsdbgMin   { get; init; } = "";
    public string PreferredVsdbg     { get; init; } = "";
    public int    ProtoVersion       { get; init; }
    public int    MinProtoVersion    { get; init; }

    // Embedded in VSIX as a JSON resource
    public static VersionManifest Load()
    {
        var asm  = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("version-manifest.json"));
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<VersionManifest>(stream)!;
    }
}

public sealed record NegotiationResult(
    bool Compatible,
    int  ProtoVersion,
    bool UpgradeRecommended,
    string? Error = null);

public sealed class VersionNegotiator
{
    private static readonly VersionManifest Manifest = VersionManifest.Load();

    public async Task<NegotiationResult> NegotiateAsync(
        GrpcChannel channel, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
        PongResponse pong;
        try
        {
            pong = await client.PingAsync(new PingRequest(), cancellationToken: ct);
        }
        catch (RpcException ex)
        {
            return new NegotiationResult(false, 0, false,
                $"Could not ping daemon: {ex.Status.Detail}");
        }

        // Proto version: hard compatibility check
        if (pong.ProtoVersion < Manifest.MinProtoVersion)
            return new NegotiationResult(false, pong.ProtoVersion, false,
                $"Daemon proto version {pong.ProtoVersion} is too old. " +
                $"This VSIX requires proto version {Manifest.MinProtoVersion}+. " +
                "Update the daemon via PiDbg: Repair Connection.");

        // Daemon semver: advisory upgrade check
        var daemonVersion = pong.DaemonVersion.Semver;
        var upgrade = IsVersionLessThan(daemonVersion, Manifest.PreferredDaemon);

        return new NegotiationResult(
            Compatible:         true,
            ProtoVersion:       pong.ProtoVersion,
            UpgradeRecommended: upgrade,
            Error:              null);
    }
}
```

`version-manifest.json` (embedded resource):
```json
{
  "vsixVersion":       "1.0.0",
  "requiredDaemonMin": "1.0.0",
  "requiredDaemonMax": "1.x.x",
  "preferredDaemon":   "1.0.0",
  "requiredVsdbgMin":  "17.0.0",
  "preferredVsdbg":    "17.12.11230",
  "protoVersion":      1,
  "minProtoVersion":   1
}
```

**Edge cases**:
- Proto version `0` means the daemon is very old (pre-versioning). Treat as incompatible.
- `"1.x.x"` max version wildcard: a daemon newer than `1.x.x` (e.g. `2.0.0`) is
  allowed with a warning, not blocked. Breaking changes will bump `minProtoVersion`.
- The `version-manifest.json` must be kept in sync manually when proto versions change.
  Add a CI check that validates the manifest against the actual proto files.

**Testing requirements**:
- Unit test: `ProtoVersion < MinProtoVersion` → `Compatible = false`
- Unit test: `ProtoVersion >= MinProtoVersion` → `Compatible = true`
- Unit test: daemon version < preferred → `UpgradeRecommended = true`
- Unit test: `VersionManifest.Load()` returns non-null with all fields

**Definition of done**:
- [x] `version-manifest.json` embedded in VSIX with correct values
- [x] `VersionManifest.Load()` reads from embedded resource
- [x] Proto version check: `< MinProtoVersion` → blocks with clear message
- [x] Daemon semver: `< PreferredDaemon` → sets `UpgradeRecommended = true` (advisory)
- [ ] All unit tests pass
