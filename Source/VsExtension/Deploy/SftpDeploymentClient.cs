using Meadow.Daemon.Contracts.V1;

using PiDbg.Infrastructure;

namespace PiDbg.Deploy;

// Orchestrates a full deployment: dotnet publish → gRPC BeginDeployment →
// 4-channel parallel SFTP upload → gRPC CommitDeployment.
// Implemented in Phase 4 (P4.8).
internal sealed class SftpDeploymentClient
{
    private const int SftpChannelCount = 4;

    private readonly MeadowDaemonService.MeadowDaemonServiceClient _grpc;
    private readonly SshConnectionManager _ssh;

    public SftpDeploymentClient(
        MeadowDaemonService.MeadowDaemonServiceClient grpc,
        SshConnectionManager ssh)
    {
        _grpc = grpc;
        _ssh = ssh;
    }

    public Task<DeploymentResult> DeployAsync(
        string publishDir,
        string appName,
        DeploymentSlot slot,
        IProgress<DeploymentProgress> progress,
        CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 4");
}

internal sealed class DeploymentResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public string? VersionLabel { get; set; }
}

internal sealed class DeploymentProgress
{
    public int FilesUploaded { get; set; }
    public int FilesTotal { get; set; }
    public long BytesUploaded { get; set; }
    public long BytesTotal { get; set; }
}
