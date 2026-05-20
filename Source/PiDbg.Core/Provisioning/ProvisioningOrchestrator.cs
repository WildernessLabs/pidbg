using System.Collections.Concurrent;

using Grpc.Core;

using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

public static class ProvisioningOrchestrator
{
    private static readonly ConcurrentDictionary<string, CachedProvisioning> _cache =
        new ConcurrentDictionary<string, CachedProvisioning>(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(10);

    private sealed class CachedProvisioning
    {
        public string DaemonVersion { get; set; } = "";
        public string VsdbgVersion { get; set; } = "";
        public DateTimeOffset At { get; set; }
    }

    public static async Task<ProvisioningResult> ProvisionAsync(
        SshSession session,
        IGrpcChannelFactory channelFactory,
        IProgress<string> progress,
        string rootFolder,
        CancellationToken ct)
    {
        var steps = new List<ProvisioningStep>();

        // --- Step 1: Capability Detection ---
        progress.Report("[1/7] Detecting device capabilities...");
        DetectionResult detection;
        try
        {
            detection = await CapabilityDetector.DetectAsync(session, rootFolder, ct).ConfigureAwait(false);
            progress.Report($"  Host: {detection.Host.OsPretty} ({detection.Host.Arch})");
            steps.Add(MakeStep("Detection", true,
                $"Host: {detection.Host.OsPretty} ({detection.Host.Arch})"));
        }
        catch (ProvisioningException ex)
        {
            return Fail(steps, "Detection", ex.Message);
        }

        // --- Step 2: Platform Validation ---
        progress.Report("[2/7] Validating platform...");
        var report = PlatformValidator.Validate(detection, rootFolder);
        foreach (var item in report.Items)
            progress.Report(
                $"  [{(item.Passed ? "OK" : item.IsFatal ? "FAIL" : "WARN")}] " +
                $"{item.Check}: {item.Message}");

        if (!report.AllFatalsPassed)
            return Fail(steps, "Platform validation",
                "Platform check failed: " +
                string.Join(", ", report.Failures.Select(f => f.Check)));

        steps.Add(MakeStep("Platform validation", true,
            $"{report.Warnings.Count} warning(s)"));

        // --- Step 3: .NET runtime ---
        progress.Report("[3/7] Checking .NET runtime...");
        var dotnetRoot = detection.Runtime.DotnetRoot;
        if (string.IsNullOrEmpty(detection.Runtime.DotnetVersion))
        {
            progress.Report("  .NET not found — installing...");
            var dotnetProg = new Progress<string>(msg => progress.Report($"  {msg}"));
            try
            {
                await DotnetInstaller.InstallAsync(session, dotnetProg, ct).ConfigureAwait(false);
                dotnetRoot = session.ExpandPath("~/.dotnet");
                steps.Add(MakeStep(".NET install", true, "installed"));
            }
            catch (ProvisioningException ex)
            {
                return Fail(steps, ".NET install", ex.Message);
            }
        }
        else
        {
            progress.Report($"  .NET {detection.Runtime.DotnetVersion} found at {dotnetRoot}");
            steps.Add(MakeStep(".NET runtime", true, detection.Runtime.DotnetVersion, skipped: true));
        }

        // --- Step 4: Daemon install/sync ---
        progress.Report("[4/7] Checking daemon...");
        var action = DaemonInstaller.DetermineAction(detection);

        if (action == DaemonInstallAction.None)
        {
            progress.Report($"  Daemon {detection.Daemon.BinaryVersion} is current");

            if (!string.IsNullOrEmpty(dotnetRoot))
            {
                var svcProg = new Progress<string>(msg => progress.Report($"  {msg}"));
                try
                {
                    await DaemonInstaller.UpdateServiceAsync(
                        session, rootFolder, dotnetRoot, svcProg, ct).ConfigureAwait(false);
                    steps.Add(MakeStep("Daemon", true, "service synced"));
                }
                catch (ProvisioningException ex)
                {
                    return Fail(steps, "Daemon service update", ex.Message);
                }
            }
            else
            {
                steps.Add(MakeStep("Daemon", true, "up to date", skipped: true));
            }
        }
        else
        {
            progress.Report($"  Action required: {action}");
            var daemonProg = new Progress<string>(msg => progress.Report($"  {msg}"));
            try
            {
                await DaemonInstaller.InstallAsync(
                    session, action, rootFolder, dotnetRoot, daemonProg, ct).ConfigureAwait(false);
                steps.Add(MakeStep("Daemon install", true, action.ToString()));
            }
            catch (ProvisioningException ex)
            {
                return Fail(steps, "Daemon install", ex.Message);
            }
        }

        // --- Step 5: Open gRPC channel ---
        progress.Report("[5/7] Connecting to daemon...");
        Channel channel;
        try
        {
            channel = await channelFactory.GetOrCreateChannelAsync(session, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Fail(steps, "gRPC channel", ex.Message);
        }
        steps.Add(MakeStep("gRPC channel", true, "connected"));

        // --- Step 6: Health check ---
        progress.Report("[6/7] Waiting for daemon health...");
        var startupTimeout = action == DaemonInstallAction.None
            ? TimeSpan.FromSeconds(10)
            : TimeSpan.FromSeconds(60);
        var healthy = await DaemonInstaller.WaitForHealthAsync(
            channel, startupTimeout, ct).ConfigureAwait(false);
        if (!healthy)
        {
            var (_, status, _) = await session.ExecuteAsync(
                "systemctl --user status meadow-daemon --no-pager -l 2>&1 | tail -30",
                ct).ConfigureAwait(false);
            progress.Report($"Daemon status:\n{status}");
            return Fail(steps, "Daemon health",
                $"Daemon did not become healthy within {startupTimeout.TotalSeconds} seconds.\n{status}");
        }
        steps.Add(MakeStep("Daemon health", true, "OK"));

        // --- Step 7: Version negotiation ---
        progress.Report("[7/7] Negotiating protocol version...");
        var nego = await VersionNegotiator.NegotiateAsync(channel, ct).ConfigureAwait(false);
        if (!nego.Compatible)
            return Fail(steps, "Version negotiation", nego.Error ?? "Protocol incompatible");

        if (nego.UpgradeRecommended)
            progress.Report("  WARN: Daemon upgrade recommended. Run PiDbg: Repair Connection to update.");

        steps.Add(MakeStep("Version negotiation", true, $"proto v{nego.ProtoVersion}"));

        // --- Step 8: vsdbg ---
        progress.Report("[8/8] Checking vsdbg...");
        if (VsdbgInstallClient.NeedsInstall(detection))
        {
            var vsdbgProg = new Progress<string>(msg => progress.Report($"  {msg}"));
            try
            {
                await VsdbgInstallClient.InstallAsync(
                    session, channel, rootFolder, vsdbgProg, ct).ConfigureAwait(false);
                steps.Add(MakeStep("vsdbg install", true, "installed"));
            }
            catch (ProvisioningException ex)
            {
                return Fail(steps, "vsdbg install", ex.Message);
            }
        }
        else
        {
            progress.Report($"  vsdbg {detection.Vsdbg.Version} is current");
            steps.Add(MakeStep("vsdbg", true, "up to date", skipped: true));
        }

        _cache[session.Host] = new CachedProvisioning
        {
            DaemonVersion = detection.Daemon.BinaryVersion,
            VsdbgVersion = detection.Vsdbg.Version,
            At = DateTimeOffset.UtcNow,
        };

        var executedCount = steps.Count(s => !s.Skipped);
        progress.Report($"Provisioning complete ({executedCount} step(s) executed).");

        return new ProvisioningResult { Success = true, Steps = steps, Channel = channel };
    }

    public static bool IsCacheValid(string host, string daemonVersion, string vsdbgVersion)
    {
        if (!_cache.TryGetValue(host, out var cached)) return false;
        if (DateTimeOffset.UtcNow - cached.At > CacheMaxAge) return false;
        if (cached.DaemonVersion != daemonVersion) return false;
        return true;
    }

    public static void InvalidateCache(string host) => _cache.TryRemove(host, out _);

    private static ProvisioningResult Fail(
        List<ProvisioningStep> steps, string step, string error)
    {
        steps.Add(new ProvisioningStep { Name = step, Success = false, Message = error });
        return new ProvisioningResult { Success = false, Steps = steps, Error = error };
    }

    private static ProvisioningStep MakeStep(
        string name, bool success, string message, bool skipped = false)
        => new ProvisioningStep
        {
            Name = name,
            Success = success,
            Message = message,
            Skipped = skipped,
        };
}
