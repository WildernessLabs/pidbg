using System;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

internal static class DotnetInstaller
{
    public static async Task InstallAsync(
        SshSession session,
        IProgress<string> progress,
        CancellationToken ct)
    {
        progress.Report("Downloading and running dotnet-install.sh...");

        var cmd = "wget --inet4-only https://dot.net/v1/dotnet-install.sh -O - " +
                  "| bash /dev/stdin --version latest 2>&1";

        var (rc, stdout, _) = await session.ExecuteAsync(
            cmd, TimeSpan.FromMinutes(10), ct).ConfigureAwait(false);

        if (rc != 0)
            throw new ProvisioningException(
                $".NET install script failed (exit {rc}):\n{stdout.Trim()}");

        progress.Report("Updating ~/.bashrc with DOTNET_ROOT...");
        var bashrcCmd =
            "grep -qF 'DOTNET_ROOT' ~/.bashrc || " +
            "echo 'export DOTNET_ROOT=$HOME/.dotnet' >> ~/.bashrc && " +
            "grep -qF '$HOME/.dotnet' ~/.bashrc || " +
            "echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc";
        await session.ExecuteAsync(bashrcCmd, ct).ConfigureAwait(false);

        progress.Report(".NET installed successfully.");
    }
}
