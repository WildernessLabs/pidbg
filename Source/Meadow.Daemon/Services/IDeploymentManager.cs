using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

public interface IDeploymentManager
{
    Task<BeginDeploymentResult> BeginDeploymentAsync(
        string appName, DeploymentManifest manifest, DeploymentSlot slot,
        string? deltaBase, CancellationToken ct);
    
    Task<CommitResult> CommitDeploymentAsync(string deploymentId, CancellationToken ct);
    
    Task AbortDeploymentAsync(string deploymentId, CancellationToken ct);
    
    Task<IReadOnlyList<string>> ListVersionsAsync(string appName);
    
    Task<string?> GetActiveVersionAsync(string appName);
    
    Task SetActiveVersionAsync(string appName, string versionId, CancellationToken ct);
    
    Task<RollbackResult> RollbackAsync(string appName, CancellationToken ct);

    Task DeleteVersionAsync(string appName, string versionId);
    
    Task PruneAsync(string appName, int retentionCount, CancellationToken ct);
}

public record BeginDeploymentResult(
    string DeploymentId,
    string StagingDir,
    IReadOnlyList<string> FilesNeeded   // paths that must be uploaded (not hard-linked)
);

public record CommitResult(
    bool Success, 
    string? ErrorMessage, 
    IReadOnlyList<ManifestVerifier.FileVerification>? Failures);
