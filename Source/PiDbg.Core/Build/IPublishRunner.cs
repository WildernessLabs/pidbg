using Meadow.Daemon.Contracts.V1;

namespace PiDbg.Build;

public sealed record PublishResult
{
    public required string PublishDir { get; init; }
    public required DeploymentManifest Manifest { get; init; }
    public required TimeSpan Duration { get; init; }
}

public interface IPublishRunner
{
    Task<PublishResult> PublishAsync(
        string projectPath,
        string appName,
        IProgress<string> output,
        CancellationToken ct);
}
