using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

// Executes the detect.sh heredoc over SSH and parses the JSON capability report.
// Implemented in Phase 6 (P6.1).
internal sealed class CapabilityDetector
{
    private readonly SshConnectionManager _ssh;

    public CapabilityDetector(SshConnectionManager ssh) => _ssh = ssh;

    public Task<CapabilityReport> DetectAsync(CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 6");
}

internal sealed class CapabilityReport
{
    public bool DaemonInstalled { get; init; }
    public string? DaemonVersion { get; init; }
    public bool DaemonRunning { get; init; }
    public bool VsdbgInstalled { get; init; }
    public string? VsdbgVersion { get; init; }
    public string? Architecture { get; init; }
    public string? DotnetVersion { get; init; }
    public bool SystemdAvailable { get; init; }
    public bool SudoAvailable { get; init; }
}
