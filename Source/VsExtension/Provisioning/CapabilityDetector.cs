using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class CapabilityDetector
{
    private static readonly string DetectScript = LoadEmbeddedScript();
    private static readonly JsonSerializerOptions JsonOpts =
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    public static async Task<DetectionResult> DetectAsync(SshSession session, CancellationToken ct)
    {
        // Inject script via stdin heredoc — avoids a temporary SFTP file upload
        var heredoc = $"bash -s <<'DETECT_SCRIPT'\n{DetectScript}\nDETECT_SCRIPT";
        var (_, stdout, stderr) = await session.ExecuteAsync(heredoc, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(stdout))
            throw new ProvisioningException(
                "Capability detection returned no output. " +
                $"Stderr: {Truncate(stderr, 300)}");

        // Strip SSH login banners by finding the first '{' on stdout
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
        var asm  = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("detect.sh", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "detect.sh not found as an embedded resource in the VSIX assembly.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max) + "...";
}
