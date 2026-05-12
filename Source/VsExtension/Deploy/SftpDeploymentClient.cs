using System.IO;

using Grpc.Core;

using Meadow.Daemon.Contracts.V1;

using PiDbg.Infrastructure;

namespace PiDbg.Deploy;

internal sealed class DeploymentException : Exception
{
    public DeploymentException(string message) : base(message) { }
}

internal sealed record DeploymentProgress
{
    public string Phase { get; }
    public long BytesSent { get; }
    public long TotalBytes { get; }
    public DeploymentProgress(string Phase, long BytesSent, long TotalBytes)
    {
        this.Phase = Phase;
        this.BytesSent = BytesSent;
        this.TotalBytes = TotalBytes;
    }

    public int PercentComplete => TotalBytes == 0 ? 100 : (int)(BytesSent * 100 / TotalBytes);
}

internal sealed class SftpDeploymentClient
{
    private readonly SshSession _session;
    private readonly Channel _channel;
    private readonly IOutputWindowService _output;

    public SftpDeploymentClient(
        SshSession session, Channel channel, IOutputWindowService output)
    {
        _session = session;
        _channel = channel;
        _output = output;
    }

    public async Task DeployAsync(
        string appName, string publishDir, DeploymentManifest manifest,
        IProgress<DeploymentProgress> progress, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(_channel);

        _output.WriteLine(OutputPane.PiDbg, $"Beginning deployment of {appName}...");

        var beginResp = await client.BeginDeploymentAsync(new BeginDeploymentRequest
        {
            AppName = appName,
            Manifest = manifest,
            Slot = DeploymentSlot.Debug,
            DeltaBase = "debug",
        }, cancellationToken: ct).ConfigureAwait(false);

        var deploymentId = beginResp.DeploymentId;
        var stagingDir = beginResp.StagingDir;
        var needed = new HashSet<string>(beginResp.FilesNeeded, StringComparer.Ordinal);

        var filesToUpload = manifest.Files.Where(f => needed.Contains(f.Path)).ToList();
        var totalBytes = filesToUpload.Sum(f => f.SizeBytes);

        _output.WriteLine(OutputPane.PiDbg,
            $"Uploading {filesToUpload.Count}/{manifest.Files.Count} files " +
            $"({manifest.Files.Count - filesToUpload.Count} unchanged, " +
            $"{totalBytes / 1024:N0} KB to transfer)");

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

            _output.WriteLine(OutputPane.PiDbg, "Deployment committed successfully.");
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

    private async Task UploadParallelAsync(
        List<FileEntry> files, string publishDir, string stagingDir,
        long totalBytes, IProgress<DeploymentProgress> progress, CancellationToken ct)
    {
        var sem = new SemaphoreSlim(4, 4);
        var counter = new long[1]; // array element is Interlocked-friendly on net472

        var tasks = files.Select(entry => UploadOneAsync(entry)).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        async Task UploadOneAsync(FileEntry entry)
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var localPath = Path.Combine(
                    publishDir, entry.Path.Replace('/', Path.DirectorySeparatorChar));
                var remotePath = $"{stagingDir}/{entry.Path}";

                using (var stream = File.OpenRead(localPath))
                {
                    await _session.UploadFileAsync(stream, remotePath, null, ct)
                                  .ConfigureAwait(false);
                }

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
