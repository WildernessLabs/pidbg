using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Meadow.Daemon.Contracts.V1;

using PiDbg.Infrastructure;

namespace PiDbg.Build;

internal sealed class PublishResult
{
    public string PublishDir { get; set; } = "";
    public DeploymentManifest Manifest { get; set; } = new DeploymentManifest();
    public TimeSpan Duration { get; set; }
}

internal sealed class PublishException : Exception
{
    public PublishException(string message) : base(message) { }
}

internal sealed class PublishService
{
    private readonly IOutputWindowService _output;

    public PublishService(IOutputWindowService output)
    {
        _output = output;
    }

    public async Task<PublishResult> PublishAsync(
        string projectPath, string appName,
        IProgress<string> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var publishDir = Path.Combine(Path.GetTempPath(), "pidbg-publish", appName);

        if (Directory.Exists(publishDir))
            Directory.Delete(publishDir, recursive: true);
        Directory.CreateDirectory(publishDir);

        var args = string.Join(" ", new[]
        {
            "publish",
            $"\"{projectPath}\"",
            "-c",   "Debug",
            "-r",   "linux-arm64",
            "--no-self-contained",
            "--output", $"\"{publishDir}\"",
            "/p:Optimize=false",
            "/p:DebugType=portable",
            "/p:EmbedAllSources=true",
            "/p:Deterministic=true",
        });

        var psi = new ProcessStartInfo("dotnet", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var tcs = new TaskCompletionSource<int>();
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _output.WriteLine(OutputPane.PiDbg, e.Data);
            progress.Report(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            _output.WriteError(OutputPane.PiDbg, e.Data);
        };
        proc.Exited += (_, _) =>
        {
            var code = proc.ExitCode;
            tcs.TrySetResult(code);
        };

        using (proc)
        using (ct.Register(() =>
        {
            try { proc.Kill(); } catch { }
            tcs.TrySetCanceled();
        }))
        {
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            var exitCode = await tcs.Task.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            // Flush any remaining output events (returns immediately; process already exited)
            proc.WaitForExit();

            if (exitCode != 0)
                throw new PublishException(
                    $"dotnet publish failed with exit code {exitCode}");
        }

        var manifest = await BuildManifestAsync(publishDir, appName, ct).ConfigureAwait(false);
        return new PublishResult
        {
            PublishDir = publishDir,
            Manifest = manifest,
            Duration = sw.Elapsed,
        };
    }

    private static async Task<DeploymentManifest> BuildManifestAsync(
        string publishDir, string appName, CancellationToken ct)
    {
        var files = Directory.GetFiles(publishDir, "*", SearchOption.AllDirectories);
        var entries = new List<FileEntry>(files.Length);

        // Uri.MakeRelativeUri is the net472-compatible alternative to Path.GetRelativePath
        var baseUri = new Uri(publishDir.TrimEnd('\\', '/') + "/");

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Uri.UnescapeDataString(
                baseUri.MakeRelativeUri(new Uri(file)).ToString())
                .Replace('\\', '/');

            var sha256 = await ComputeSha256Async(file, ct).ConfigureAwait(false);
            entries.Add(new FileEntry
            {
                Path      = relativePath,
                Sha256    = sha256,
                SizeBytes = new FileInfo(file).Length,
            });
        }

        var manifest = new DeploymentManifest
        {
            AppName      = appName,
            Version      = "debug",   // debug slot always uses the "debug" version label
            Slot         = DeploymentSlot.Debug,
            EntryPoint   = $"{appName}.dll",
            DeployedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        manifest.Files.AddRange(entries);
        manifest.ManifestSha256 = ComputeManifestHash(manifest);
        return manifest;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(fs);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }, ct).ConfigureAwait(false);
    }

    private static string ComputeManifestHash(DeploymentManifest manifest)
    {
        var sb = new StringBuilder();
        foreach (var f in manifest.Files)
            sb.Append(f.Path).Append(':').Append(f.Sha256).Append('\n');

        using (var sha = SHA256.Create())
        {
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }
    }

}
