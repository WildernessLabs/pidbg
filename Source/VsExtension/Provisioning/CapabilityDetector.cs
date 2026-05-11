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
    public bool DaemonInstalled { get; set; }
    public string? DaemonVersion { get; set; }
    public bool DaemonRunning { get; set; }
    public bool VsdbgInstalled { get; set; }
    public string? VsdbgVersion { get; set; }
    public string? Architecture { get; set; }
    public string? DotnetVersion { get; set; }
    public bool SystemdAvailable { get; set; }
    public bool SudoAvailable { get; set; }
}
