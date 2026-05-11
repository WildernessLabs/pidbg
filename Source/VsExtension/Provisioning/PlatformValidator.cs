namespace PiDbg.Provisioning;

// Pure validation logic (no I/O) — validates a CapabilityReport against
// minimum platform requirements. Fully unit-testable.
// Implemented in Phase 6 (P6.2).
internal sealed class PlatformValidator
{
    public static readonly Version MinDotnetVersion = new(9, 0);
    public const string RequiredArchitecture = "arm64";

    public ValidationResult Validate(CapabilityReport report)
        => throw new NotImplementedException("Implemented in Phase 6");
}

internal sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = [];
    public IReadOnlyList<string> Warnings { get; set; } = [];
}
