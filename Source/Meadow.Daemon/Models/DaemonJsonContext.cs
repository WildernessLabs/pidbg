using System.Text.Json.Serialization;
using Meadow.Daemon.Models;

namespace Meadow.Daemon.Models;

[JsonSerializable(typeof(AppRecord))]
[JsonSerializable(typeof(DebugSessionRecord))]
[JsonSerializable(typeof(DaemonState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DaemonJsonContext : JsonSerializerContext { }
