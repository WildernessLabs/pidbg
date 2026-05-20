using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Meadow.Daemon.Contracts.V1;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using PiDbg.Infrastructure;

namespace PiDbg.Deploy;

public sealed class DeploymentException : Exception
{
    public DeploymentException(string message) : base(message) { }
}

public sealed record DeploymentProgress(string Phase, long BytesSent, long TotalBytes)
{
    public int PercentComplete => TotalBytes == 0 ? 100 : (int)(BytesSent * 100 / TotalBytes);
}

public sealed partial class SftpDeploymentClient
{
    private readonly SshSession _session;
    private readonly ChannelBase _channel;
    private readonly ILogger    _logger;

    public SftpDeploymentClient(SshSession session, ChannelBase channel, ILogger logger)
    {
        _session = session;
        _channel = channel;
        _logger  = logger;
    }

    public async Task DeployAsync(
        string appName, string publishDir, DeploymentManifest manifest,
        IProgress<DeploymentProgress> progress, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(_channel);

        LogBeginDeployment(_logger, appName);

        var beginResp = await client.BeginDeploymentAsync(new BeginDeploymentRequest
        {
            AppName   = appName,
            Manifest  = manifest,
            Slot      = DeploymentSlot.Debug,
            DeltaBase = "debug",
        }, cancellationToken: ct).ConfigureAwait(false);

        var deploymentId  = beginResp.DeploymentId;
        var stagingDir    = beginResp.StagingDir;
        var needed        = new HashSet<string>(beginResp.FilesNeeded, StringComparer.Ordinal);
        var filesToUpload = manifest.Files.Where(f => needed.Contains(f.Path)).ToList();
        var totalBytes    = filesToUpload.Sum(f => f.SizeBytes);

        LogUploadPlan(_logger, filesToUpload.Count, manifest.Files.Count,
            manifest.Files.Count - filesToUpload.Count, totalBytes / 1024);

        try
        {
            await UploadParallelAsync(
                filesToUpload, publishDir, stagingDir,
                totalBytes, progress, ct).ConfigureAwait(false);

            progress.Report(new DeploymentProgress("Verifying", totalBytes, totalBytes));

            var commitResp = await client.CommitDeploymentAsync(
                new CommitDeploymentRequest { DeploymentId = deploymentId },
                cancellationToken: ct).ConfigureAwait(false);

            if (!commitResp.Success)
            {
                var failures = string.Join(", ", commitResp.Failures.Select(f => f.Path));
                throw new DeploymentException(
                    $"Deployment verification failed for: {failures}. {commitResp.ErrorMessage}");
            }

            LogDeploymentCommitted(_logger);
        }
        catch (OperationCanceledException)
        {
            _ = client.AbortDeploymentAsync(
                new AbortDeploymentRequest { DeploymentId = deploymentId },
                cancellationToken: CancellationToken.None);
            throw;
        }
        catch
        {
            try
            {
                await client.AbortDeploymentAsync(
                    new AbortDeploymentRequest { DeploymentId = deploymentId },
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* best-effort abort */ }
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Beginning deployment of {AppName}")]
    private static partial void LogBeginDeployment(ILogger logger, string appName);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Uploading {UploadCount}/{TotalCount} files ({SkipCount} unchanged, {Kb} KB to transfer)")]
    private static partial void LogUploadPlan(ILogger logger, int uploadCount, int totalCount, int skipCount, long kb);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deployment committed successfully")]
    private static partial void LogDeploymentCommitted(ILogger logger);

    private async Task UploadParallelAsync(
        List<FileEntry> files, string publishDir, string stagingDir,
        long totalBytes, IProgress<DeploymentProgress> progress, CancellationToken ct)
    {
        var sem     = new SemaphoreSlim(4, 4);
        var counter = new long[1];

        var tasks = files.Select(UploadOneAsync).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task UploadOneAsync(FileEntry entry)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var localPath  = Path.Combine(publishDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var remotePath = $"{stagingDir}/{entry.Path}";

                using (var stream = File.OpenRead(localPath))
                    await _session.UploadFileAsync(stream, remotePath, null, ct).ConfigureAwait(false);

                var uploaded = Interlocked.Add(ref counter[0], entry.SizeBytes);
                progress.Report(new DeploymentProgress("Uploading", uploaded, totalBytes));
            }
            finally
            {
                sem.Release();
            }
        }
    }
}
