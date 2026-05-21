using System.Diagnostics;
using System.Security.Cryptography;
using Meadow.Daemon.Contracts.V1;
using Microsoft.Extensions.Logging;

namespace PiDbg.Build;

public sealed class CliPublishRunner : IPublishRunner
{
    private readonly ILogger<CliPublishRunner> _logger;

    public CliPublishRunner(ILogger<CliPublishRunner> logger)
    {
        _logger = logger;
    }

    public async Task<PublishResult> PublishAsync(
        string projectPath,
        string appName,
        IProgress<string> output,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
            throw new FileNotFoundException("Project file not found.", projectPath);

        var publishDir = Path.Combine(
            Path.GetTempPath(),
            "pidbg-publish",
            appName,
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(publishDir);

        var args =
            $"publish \"{projectPath}\" -c Debug -r linux-arm64 --self-contained false -o \"{publishDir}\"";

        output.Report("Running dotnet publish...");
        _logger.LogInformation("Executing: dotnet {Args}", args);

        var sw = Stopwatch.StartNew();
        await RunProcessAsync("dotnet", args, output, ct).ConfigureAwait(false);
        sw.Stop();

        var manifest = BuildManifest(publishDir, appName, output, ct);

        return new PublishResult
        {
            PublishDir = publishDir,
            Manifest = manifest,
            Duration = sw.Elapsed,
        };
    }

    private static async Task RunProcessAsync(
        string fileName,
        string arguments,
        IProgress<string> output,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdErr = new List<string>();

        var outDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var errDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                outDone.TrySetResult(true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(e.Data))
                output.Report(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                errDone.TrySetResult(true);
                return;
            }

            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                stdErr.Add(e.Data);
                output.Report(e.Data);
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Failed to start dotnet publish process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cancellation.
            }
        });

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await Task.WhenAll(outDone.Task, errDone.Task).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var tail = string.Join(Environment.NewLine, stdErr.TakeLast(8));
            throw new InvalidOperationException(
                $"dotnet publish failed (exit {process.ExitCode}).{Environment.NewLine}{tail}");
        }
    }

    private static DeploymentManifest BuildManifest(
        string publishDir,
        string appName,
        IProgress<string> output,
        CancellationToken ct)
    {
        output.Report("Creating deployment manifest...");

        var files = Directory
            .EnumerateFiles(publishDir, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => CreateFileEntry(publishDir, path, ct))
            .ToList();

        var manifest = new DeploymentManifest
        {
            AppName = appName,
            Version = "debug",
            Slot = DeploymentSlot.Debug,
            EntryPoint = $"{appName}.dll",
            StartupArgs = string.Empty,
            DeployedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ManifestSha256 = string.Empty,
        };

        manifest.Files.AddRange(files);
        return manifest;
    }

    private static FileEntry CreateFileEntry(string publishDir, string absolutePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var relative = Path.GetRelativePath(publishDir, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

        var fileInfo = new FileInfo(absolutePath);

        using var stream = File.OpenRead(absolutePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(stream);

        return new FileEntry
        {
            Path = relative,
            SizeBytes = fileInfo.Length,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant(),
        };
    }
}
