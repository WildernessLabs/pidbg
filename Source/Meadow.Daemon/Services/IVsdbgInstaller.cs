using System.Runtime.Versioning;

namespace Meadow.Daemon.Services;

public interface IVsdbgInstaller
{
    Task<bool> IsInstalledAsync(string requiredVersion);
    [SupportedOSPlatform("linux")]
    Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct);
    [SupportedOSPlatform("linux")]
    Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256, IProgress<string> progress, CancellationToken ct);
    string? GetInstalledVersion();
}

public class VsdbgInstallException : Exception
{
    public VsdbgInstallException(string message) : base(message) { }
    public VsdbgInstallException(string message, Exception inner) : base(message, inner) { }
}
