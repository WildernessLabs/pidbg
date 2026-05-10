using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PiDbg.Infrastructure;

// Reads per-project properties (Pi host, port, app name) from the
// project file via IVsBuildPropertyStorage.
// Implemented in Phase 4 (P4.4).
internal sealed class ProjectPropertyReader
{
    public const string PropertyCategory = "PiDbg";

    public const string PropHost = "PiDbgHost";
    public const string PropPort = "PiDbgPort";
    public const string PropUsername = "PiDbgUsername";
    public const string PropAppName = "PiDbgAppName";
    public const string PropPrivateKeyPath = "PiDbgPrivateKeyPath";

    private readonly IVsBuildPropertyStorage _storage;

    public ProjectPropertyReader(IVsBuildPropertyStorage storage) => _storage = storage;

    public string? GetProperty(string name)
        => throw new NotImplementedException("Implemented in Phase 4");

    public void SetProperty(string name, string value)
        => throw new NotImplementedException("Implemented in Phase 4");
}
