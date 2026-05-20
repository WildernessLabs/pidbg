using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class CapabilityDetector
{
    private static readonly string DetectScript = LoadEmbeddedScript()
        .Replace("\r\n", "\n").Replace("\r", "\n");
    private static readonly JsonSerializerOptions JsonOpts =
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public static async Task<DetectionResult> DetectAsync(
        SshSession session, string rootFolder, CancellationToken ct)
    {
        var escaped = rootFolder.Replace("'", "'\\''");
        var heredoc = $"ROOT_FOLDER='{escaped}' bash -s <<'DETECT_SCRIPT'\n{DetectScript}\nDETECT_SCRIPT";
        var (_, stdout, stderr) = await session.ExecuteAsync(heredoc, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
            throw new ProvisioningException(
                "Capability detection returned no output. " +
                $"Stderr: {Truncate(stderr, 300)}");

        var jsonStart = stdout.IndexOf('{');
        if (jsonStart < 0)
            throw new ProvisioningException(
                $"Detection returned no JSON.\nOutput was: {Truncate(stdout, 200)}");

        var json = stdout.Substring(jsonStart);

        try
        {
            var result = JsonSerializer.Deserialize<DetectionResult>(json, JsonOpts);
            return result ?? throw new ProvisioningException("Detection returned null JSON");
        }
        catch (JsonException ex)
        {
            throw new ProvisioningException(
                $"Detection returned invalid JSON: {ex.Message}\n" +
                $"Output was: {Truncate(json, 200)}");
        }
    }

    private static string LoadEmbeddedScript()
    {
        var asm  = typeof(CapabilityDetector).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("detect.sh", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "detect.sh not found as an embedded resource in PiDbg.Core.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
