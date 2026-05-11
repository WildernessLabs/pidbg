using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

internal class DeploymentManager : IDeploymentManager
{
    private readonly VersionStore _versionStore;
    private readonly StagingController _staging;
    private readonly ManifestVerifier _verifier;
    private readonly DaemonOptions _options;
    private readonly ILogger<DeploymentManager> _logger;

    // Per-app lock to ensure serial deployments per application
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _appLocks = new();
    
    // Track in-progress deployments
    private readonly ConcurrentDictionary<string, ActiveDeployment> _activeDeployments = new();

    public DeploymentManager(
        VersionStore versionStore,
        StagingController staging,
        ManifestVerifier verifier,
        IOptions<DaemonOptions> options,
        ILogger<DeploymentManager> logger)
    {
        _versionStore = versionStore;
        _staging = staging;
        _verifier = verifier;
        _options = options.Value;
        _logger = logger;
    }

    private sealed record ActiveDeployment(
        string DeploymentId,
        string AppName,
        string StagingDir,
        DeploymentManifest Manifest,
        DeploymentSlot Slot,
        SemaphoreSlim AppLock,
        DateTime StartedAt
    );

    public async Task<BeginDeploymentResult> BeginDeploymentAsync(
        string appName, DeploymentManifest manifest, DeploymentSlot slot,
        string? deltaBase, CancellationToken ct)
    {
        DaemonPaths.SanitizeName(appName);
        
        var appLock = _appLocks.GetOrAdd(appName, _ => new SemaphoreSlim(1, 1));

        if (slot == DeploymentSlot.Debug)
        {
            // Cancel-and-Replace for debug slot
            var existing = _activeDeployments.Values.FirstOrDefault(d => d.AppName == appName && d.Slot == slot);
            if (existing != null)
            {
                _logger.LogInformation("Aborting existing debug deployment {DeploymentId} for {App} to start new one", 
                    existing.DeploymentId, appName);
                await AbortDeploymentAsync(existing.DeploymentId, ct);
            }
        }

        await appLock.WaitAsync(ct);

        try
        {
            var deploymentId = Guid.NewGuid().ToString("N"); // ULID would be better, GUID is fine for ID
            var stagingDir = _staging.CreateStaging(appName);

            // Delta resolution
            string? sourceDir = deltaBase switch
            {
                "debug"      => DaemonPaths.AppDebugDir(_options, appName),
                not null     => DaemonPaths.AppVersionDir(_options, appName, deltaBase),
                null         => null
            };

            HashSet<string> linked = sourceDir is not null && Directory.Exists(sourceDir)
                ? await _staging.HardLinkUnchangedFilesAsync(appName, sourceDir, manifest, ct)
                : [];

            var filesNeeded = manifest.Files
                .Where(f => !linked.Contains(f.Path))
                .Select(f => f.Path)
                .ToList();

            var active = new ActiveDeployment(
                deploymentId, appName, stagingDir, manifest, slot, appLock, DateTime.UtcNow);
            
            _activeDeployments[deploymentId] = active;

            return new BeginDeploymentResult(deploymentId, stagingDir, filesNeeded);
        }
        catch
        {
            appLock.Release();
            throw;
        }
    }

    public async Task<CommitResult> CommitDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        if (!_activeDeployments.TryRemove(deploymentId, out var deployment))
            return new CommitResult(false, "Deployment not found", null);

        try
        {
            // Verify integrity
            var verifyResult = await _verifier.VerifyAsync(deployment.StagingDir, deployment.Manifest, ct);
            if (!verifyResult.Success)
            {
                _logger.LogWarning("Deployment {DeploymentId} for {App} failed verification", deploymentId, deployment.AppName);
                _staging.CleanStaging(deployment.AppName);
                return new CommitResult(false, "Verification failed", verifyResult.Files.Where(f => !f.Passed).ToList());
            }

            // Write manifest.json to staging before activation
            var manifestPath = Path.Combine(deployment.StagingDir, "manifest.json");
            await using (var fs = File.Create(manifestPath))
            {
                await JsonSerializer.SerializeAsync(fs, deployment.Manifest, 
                    Meadow.Daemon.Models.DaemonJsonContext.Default.DeploymentManifest, ct);
            }

            // Activate
            if (deployment.Slot == DeploymentSlot.Debug)
            {
                await ActivateDebugSlotAsync(deployment.AppName, deployment.StagingDir, ct);
            }
            else
            {
                ActivateProductionSlot(deployment.AppName, deployment.Manifest.Version, deployment.StagingDir);
                
                // Auto-prune after production commit
                await PruneAsync(deployment.AppName, _options.DeploymentRetentionCount, ct);
            }

            return new CommitResult(true, null, null);
        }
        finally
        {
            deployment.AppLock.Release();
        }
    }

    public async Task AbortDeploymentAsync(string deploymentId, CancellationToken ct)
    {
        if (_activeDeployments.TryRemove(deploymentId, out var deployment))
        {
            try
            {
                _staging.CleanStaging(deployment.AppName);
            }
            finally
            {
                deployment.AppLock.Release();
            }
        }
        await Task.CompletedTask;
    }

    private async Task ActivateDebugSlotAsync(string appName, string stagingDir, CancellationToken ct)
    {
        var debugDir = DaemonPaths.AppDebugDir(_options, appName);
        var oldDir   = debugDir + ".old";

        _logger.LogInformation("Activating debug slot for {App}: {Staging} → {Debug}",
            appName, stagingDir, debugDir);

        if (Directory.Exists(debugDir))
        {
            if (Directory.Exists(oldDir))
                Directory.Delete(oldDir, recursive: true);

            Directory.Move(debugDir, oldDir);
        }

        Directory.Move(stagingDir, debugDir);

        if (Directory.Exists(oldDir))
        {
            _ = Task.Run(() =>
            {
                try { Directory.Delete(oldDir, recursive: true); }
                catch (Exception ex)
                { _logger.LogWarning(ex, "Failed to clean old debug dir {Dir}", oldDir); }
            }, ct);
        }

        _logger.LogInformation("Debug slot activated for {App}", appName);
        await Task.CompletedTask;
    }

    private void ActivateProductionSlot(string appName, string versionId, string stagingDir)
    {
        var versionDir = DaemonPaths.AppVersionDir(_options, appName, versionId);

        _logger.LogInformation("Activating production slot for {App} version {Version}",
            appName, versionId);

        if (Directory.Exists(versionDir))
            throw new InvalidOperationException($"Version directory already exists: {versionId}");

        Directory.Move(stagingDir, versionDir);
        _versionStore.SetActiveVersion(appName, versionId);

        _logger.LogInformation("Production version {Version} active for {App}", versionId, appName);
    }

    public Task<IReadOnlyList<string>> ListVersionsAsync(string appName)
        => Task.FromResult(_versionStore.ListVersions(appName));

    public Task<string?> GetActiveVersionAsync(string appName)
        => Task.FromResult(_versionStore.GetActiveVersion(appName));

    public Task SetActiveVersionAsync(string appName, string versionId, CancellationToken ct)
    {
        _versionStore.SetActiveVersion(appName, versionId);
        return Task.CompletedTask;
    }

    public Task<RollbackResult> RollbackAsync(string appName, CancellationToken ct)
    {
        return Task.FromResult(_versionStore.Rollback(appName));
    }

    public Task DeleteVersionAsync(string appName, string versionId)
    {
        _versionStore.DeleteVersion(appName, versionId);
        return Task.CompletedTask;
    }

    public async Task PruneAsync(string appName, int retentionCount, CancellationToken ct)
    {
        var versions = _versionStore.ListVersions(appName);
        var active   = _versionStore.GetActiveVersion(appName);

        var deletable = versions.Where(v => v != active).ToList();

        if (deletable.Count <= retentionCount)
        {
            _logger.LogDebug("Pruning {App}: {Count} versions, {Retention} retention — nothing to prune",
                appName, versions.Count, retentionCount);
            return;
        }

        var toDelete = deletable.Take(deletable.Count - retentionCount).ToList();
        foreach (var versionId in toDelete)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                _versionStore.DeleteVersion(appName, versionId);
                _logger.LogInformation("Pruned version {Version} of {App}", versionId, appName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune version {Version} of {App}", versionId, appName);
            }
        }

        // Clean orphan staging
        var stagingDir = DaemonPaths.AppStagingDir(_options, appName);
        if (Directory.Exists(stagingDir) && !_activeDeployments.Values.Any(d => d.AppName == appName))
        {
            _logger.LogWarning("Pruning orphan staging dir for {App}", appName);
            Directory.Delete(stagingDir, recursive: true);
        }
        await Task.CompletedTask;
    }
}
