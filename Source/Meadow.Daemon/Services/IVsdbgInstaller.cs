namespace Meadow.Daemon.Services;

public interface IVsdbgInstaller
{
    Task<bool> IsInstalledAsync(string requiredVersion);
    Task InstallAsync(string version, IProgress<string> progress, CancellationToken ct);
    Task InstallFromTarballAsync(
        Stream tarball, string expectedSha256, IProgress<string> progress, CancellationToken ct);
    string? GetInstalledVersion();
}

public class VsdbgInstallException : Exception
{
    public VsdbgInstallException(string message) : base(message) { }
    public VsdbgInstallException(string message, Exception inner) : base(message, inner) { }
}
