using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiDbg.Provisioning;

internal sealed class VersionManifest
{
    [JsonPropertyName("vsixVersion")]       public string VsixVersion      { get; set; } = "";
    [JsonPropertyName("requiredDaemonMin")] public string RequiredDaemonMin { get; set; } = "";
    [JsonPropertyName("requiredDaemonMax")] public string RequiredDaemonMax { get; set; } = "";
    [JsonPropertyName("preferredDaemon")]   public string PreferredDaemon   { get; set; } = "";
    [JsonPropertyName("requiredVsdbgMin")]  public string RequiredVsdbgMin  { get; set; } = "";
    [JsonPropertyName("preferredVsdbg")]    public string PreferredVsdbg    { get; set; } = "";
    [JsonPropertyName("protoVersion")]      public int    ProtoVersion      { get; set; }
    [JsonPropertyName("minProtoVersion")]   public int    MinProtoVersion   { get; set; }

    private static VersionManifest? _instance;

    public static VersionManifest Load()
    {
        if (_instance is not null) return _instance;

        var asm  = typeof(VersionManifest).Assembly;
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("version-manifest.json", StringComparison.OrdinalIgnoreCase));

        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var json = reader.ReadToEnd();

        _instance = JsonSerializer.Deserialize<VersionManifest>(json)!;
        return _instance;
    }
}

internal sealed record NegotiationResult(
    bool    Compatible,
    int     ProtoVersion,
    bool    UpgradeRecommended,
    string? Error = null);
