using Meadow.Daemon.Contracts.V1;
using PiDbg.Infrastructure;
using Renci.SshNet;

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
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? VersionLabel { get; init; }
}

internal sealed class DeploymentProgress
{
    public int FilesUploaded { get; init; }
    public int FilesTotal { get; init; }
    public long BytesUploaded { get; init; }
    public long BytesTotal { get; init; }
}
