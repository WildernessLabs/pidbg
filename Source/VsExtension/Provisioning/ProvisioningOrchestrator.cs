using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

// 7-step first-F5 provisioning state machine.
// Steps: detect → validate → install-daemon → start-daemon → install-vsdbg → verify → cache.
// Implemented in Phase 6 (P6.5).
internal sealed class ProvisioningOrchestrator
{
    private readonly CapabilityDetector _detector;
    private readonly PlatformValidator _validator;
    private readonly DaemonInstaller _daemonInstaller;
    private readonly VsdbgInstallClient _vsdbgInstaller;
    private readonly SshConnectionManager _ssh;

    public ProvisioningOrchestrator(
        CapabilityDetector detector,
        PlatformValidator validator,
        DaemonInstaller daemonInstaller,
        VsdbgInstallClient vsdbgInstaller,
        SshConnectionManager ssh)
    {
        _detector = detector;
        _validator = validator;
        _daemonInstaller = daemonInstaller;
        _vsdbgInstaller = vsdbgInstaller;
        _ssh = ssh;
    }

    public Task<ProvisioningResult> ProvisionAsync(
        IProgress<ProvisioningStep> progress, CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");
}

internal sealed class ProvisioningResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public CapabilityReport? Report { get; init; }
}

internal enum ProvisioningStep
{
    Detecting,
    Validating,
    InstallingDaemon,
    StartingDaemon,
    InstallingVsdbg,
    Verifying,
    Caching,
    Complete,
}
