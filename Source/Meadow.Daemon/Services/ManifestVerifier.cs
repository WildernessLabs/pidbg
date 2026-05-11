using System.Collections.Concurrent;
using System.Security.Cryptography;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

// Verifies the integrity of a completed staging directory by recomputing
// SHA-256 for every file listed in the manifest.
public class ManifestVerifier
{
    private readonly ILogger<ManifestVerifier> _logger;

    public ManifestVerifier(ILogger<ManifestVerifier> logger) => _logger = logger;

    public record VerificationResult(bool Success, IReadOnlyList<FileVerification> Files);
    public record FileVerification(string Path, bool Passed, string? Error);

    public async Task<VerificationResult> VerifyAsync(
        string stagingDir,
        DeploymentManifest manifest,
        CancellationToken ct)
    {
        // Verify manifest-level checksum first
        if (!VerifyManifestHash(manifest))
        {
            _logger.LogError("Manifest-level SHA-256 mismatch — manifest may be tampered");
            return new VerificationResult(false, [
                new FileVerification("manifest.json", false, "manifest hash mismatch")
            ]);
        }

        // Parallel file verification — 4 concurrent workers
        var results = new ConcurrentBag<FileVerification>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(manifest.Files, options, async (entry, innerCt) =>
        {
            // Normalise path for current OS
            var path = Path.Combine(stagingDir,
                entry.Path.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                results.Add(new FileVerification(entry.Path, false, "file missing"));
                return;
            }

            try
            {
                var actualSha256 = await ComputeSha256Async(path, innerCt);
                var passed = string.Equals(actualSha256, entry.Sha256,
                                           StringComparison.OrdinalIgnoreCase);
                if (!passed)
                    _logger.LogWarning("SHA-256 mismatch for {File}: expected {Expected} got {Actual}",
                        entry.Path, entry.Sha256, actualSha256);
                
                results.Add(new FileVerification(entry.Path, passed, passed ? null : "hash mismatch"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to verify {File}", entry.Path);
                results.Add(new FileVerification(entry.Path, false, ex.Message));
            }
        });

        var allPassed = results.All(r => r.Passed);
        _logger.LogInformation("Verification {Result}: {Pass}/{Total} files",
            allPassed ? "PASSED" : "FAILED",
            results.Count(r => r.Passed), results.Count);
            
        return new VerificationResult(allPassed, results.ToList());
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VerifyManifestHash(DeploymentManifest manifest)
    {
        if (string.IsNullOrEmpty(manifest.ManifestSha256)) return true; // not present = skip
        // TODO: Implement same canonicalisation algorithm as VSIX (Phase 3.8)
        return true; 
    }
}
