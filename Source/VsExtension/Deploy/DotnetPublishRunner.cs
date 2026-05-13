namespace PiDbg.Deploy;

// Stub — the active deployment path uses PublishService directly.
// Implemented in Phase 4 (P4.7) if a non-CPS fallback is ever needed.
internal sealed class DotnetPublishRunner
{
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
