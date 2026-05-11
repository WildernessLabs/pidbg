using PiDbg.Infrastructure;

namespace PiDbg.Deploy;

// Invokes `dotnet publish` targeting linux-arm64 for the active project.
// Implemented in Phase 4 (P4.7).
internal sealed class DotnetPublishRunner
{
    private readonly ProjectPropertyReader _projectProps;

    public DotnetPublishRunner(ProjectPropertyReader projectProps) => _projectProps = projectProps;

    public Task<PublishResult> PublishAsync(
        string projectPath,
        string configuration,
        IProgress<string> output,
        CancellationToken ct)
        => throw new NotImplementedException("Implemented in Phase 4");
}

internal sealed class PublishResult
{
    public bool Success { get; set; }
    public string? OutputDirectory { get; set; }
    public string? ErrorMessage { get; set; }
}
