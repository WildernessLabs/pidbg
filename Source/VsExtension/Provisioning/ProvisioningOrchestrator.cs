using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class ProvisioningOrchestrator
{
    private static readonly ConcurrentDictionary<string, CachedProvisioning> _cache =
        new ConcurrentDictionary<string, CachedProvisioning>(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan CacheMaxAge = TimeSpan.FromMinutes(10);

    private sealed class CachedProvisioning
    {
        public string        DaemonVersion { get; set; } = "";
        public string        VsdbgVersion  { get; set; } = "";
        public DateTimeOffset At           { get; set; }
    }

    public static async Task<ProvisioningResult> ProvisionAsync(
        SshSession session,
        IGrpcChannelFactory channelFactory,
        IOutputWindowService output,
        string rootFolder,
        CancellationToken ct)
    {
        var steps = new List<ProvisioningStep>();

        // --- Step 1: Capability Detection ---
        output.WriteLine(OutputPane.Provisioning, "[1/7] Detecting device capabilities...");
        DetectionResult detection;
        try
        {
            detection = await CapabilityDetector.DetectAsync(session, rootFolder, ct).ConfigureAwait(false);
            steps.Add(MakeStep("Detection", true,
                $"Host: {detection.Host.OsPretty} ({detection.Host.Arch})"));
        }
        catch (ProvisioningException ex)
        {
            return Fail(steps, "Detection", ex.Message);
        }

        // --- Step 2: Platform Validation ---
        output.WriteLine(OutputPane.Provisioning, "[2/7] Validating platform...");
        var report = PlatformValidator.Validate(detection, rootFolder);
        foreach (var item in report.Items)
            output.WriteLine(OutputPane.Provisioning,
                $"  [{(item.Passed ? "OK" : item.IsFatal ? "FAIL" : "WARN")}] " +
                $"{item.Check}: {item.Message}");

        if (!report.AllFatalsPassed)
            return Fail(steps, "Platform validation",
                "Platform check failed: " +
                string.Join(", ", report.Failures.Select(f => f.Check)));

        steps.Add(MakeStep("Platform validation", true,
            $"{report.Warnings.Count} warning(s)"));

        // --- Step 3: Daemon install ---
        output.WriteLine(OutputPane.Provisioning, "[3/7] Checking daemon...");
        var action = DaemonInstaller.DetermineAction(detection);

        if (action == DaemonInstallAction.None)
        {
            output.WriteLine(OutputPane.Provisioning,
                $"  Daemon {detection.Daemon.BinaryVersion} is current (no action needed)");
            steps.Add(MakeStep("Daemon", true, "up to date", skipped: true));
        }
        else
        {
            output.WriteLine(OutputPane.Provisioning, $"  Action required: {action}");
            var prog = new Progress<string>(
                msg => output.WriteLine(OutputPane.Provisioning, $"  {msg}"));
            try
            {
                await DaemonInstaller.InstallAsync(session, action, rootFolder, prog, ct).ConfigureAwait(false);
                steps.Add(MakeStep("Daemon install", true, action.ToString()));
            }
            catch (ProvisioningException ex)
            {
                return Fail(steps, "Daemon install", ex.Message);
            }
        }

        // --- Step 4: Open gRPC channel ---
        output.WriteLine(OutputPane.Provisioning, "[4/7] Connecting to daemon...");
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

        // --- Step 5: Health check ---
        output.WriteLine(OutputPane.Provisioning, "[5/7] Waiting for daemon health...");
        var startupTimeout = action == DaemonInstallAction.None
            ? TimeSpan.FromSeconds(10)   // already running — should be immediate
            : TimeSpan.FromSeconds(60);  // fresh install: first-run .NET extraction can be slow
        var healthy = await DaemonInstaller.WaitForHealthAsync(
            channel, startupTimeout, ct).ConfigureAwait(false);
        if (!healthy)
        {
            var (_, status, _) = await session.ExecuteAsync(
                "systemctl --user status meadow-daemon --no-pager -l 2>&1 | tail -30",
                ct).ConfigureAwait(false);
            output.WriteLine(OutputPane.Provisioning,
                $"Daemon status:\n{status}");
            return Fail(steps, "Daemon health",
                $"Daemon did not become healthy within 10 seconds.\n{status}");
        }
        steps.Add(MakeStep("Daemon health", true, "OK"));

        // --- Step 6: Version negotiation ---
        output.WriteLine(OutputPane.Provisioning, "[6/7] Negotiating protocol version...");
        var nego = await VersionNegotiator.NegotiateAsync(channel, ct).ConfigureAwait(false);
        if (!nego.Compatible)
            return Fail(steps, "Version negotiation", nego.Error ?? "Protocol incompatible");

        if (nego.UpgradeRecommended)
            output.WriteWarning(OutputPane.Provisioning,
                "  Daemon upgrade recommended. Run PiDbg: Repair Connection to update.");

        steps.Add(MakeStep("Version negotiation", true, $"proto v{nego.ProtoVersion}"));

        // --- Step 7: vsdbg ---
        output.WriteLine(OutputPane.Provisioning, "[7/7] Checking vsdbg...");
        if (VsdbgInstallClient.NeedsInstall(detection))
        {
            var vsdbgProg = new Progress<string>(
                msg => output.WriteLine(OutputPane.Provisioning, $"  {msg}"));
            try
            {
                await VsdbgInstallClient.InstallAsync(
                    session, channel, detection.Runtime.CurlAvailable, vsdbgProg, ct)
                    .ConfigureAwait(false);
                steps.Add(MakeStep("vsdbg install", true, "installed"));
            }
            catch (ProvisioningException ex)
            {
                return Fail(steps, "vsdbg install", ex.Message);
            }
        }
        else
        {
            output.WriteLine(OutputPane.Provisioning,
                $"  vsdbg {detection.Vsdbg.Version} is current");
            steps.Add(MakeStep("vsdbg", true, "up to date", skipped: true));
        }

        // Cache result to skip no-op provisioning on subsequent F5
        _cache[session.Host] = new CachedProvisioning
        {
            DaemonVersion = detection.Daemon.BinaryVersion,
            VsdbgVersion  = detection.Vsdbg.Version,
            At            = DateTimeOffset.UtcNow,
        };

        var executedCount = steps.Count(s => !s.Skipped);
        output.WriteLine(OutputPane.Provisioning,
            $"Provisioning complete ({executedCount} step(s) executed).");

        return new ProvisioningResult { Success = true, Steps = steps, Channel = channel };
    }

    public static bool IsCacheValid(string host, string daemonVersion, string vsdbgVersion)
    {
        if (!_cache.TryGetValue(host, out var cached)) return false;
        if (DateTimeOffset.UtcNow - cached.At > CacheMaxAge)    return false;
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
            Name    = name,
            Success = success,
            Message = message,
            Skipped = skipped,
        };
}
