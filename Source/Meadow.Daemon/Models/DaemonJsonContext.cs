using System.Text.Json.Serialization;

namespace Meadow.Daemon.Models;

[JsonSerializable(typeof(AppsState))]
[JsonSerializable(typeof(SessionsState))]
[JsonSerializable(typeof(AppRecord))]
[JsonSerializable(typeof(DebugSessionRecord))]
[JsonSerializable(typeof(Meadow.Daemon.RestCompat.MeadowRestCompatController.HealthResponse))]
[JsonSerializable(typeof(Meadow.Daemon.Contracts.V1.DeploymentManifest))]
[JsonSerializable(typeof(Meadow.Daemon.Contracts.V1.DeploymentRecord))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DaemonJsonContext : JsonSerializerContext { }
