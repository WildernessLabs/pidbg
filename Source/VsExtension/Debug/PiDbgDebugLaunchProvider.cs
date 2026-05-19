using System.ComponentModel.Composition;

using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.Debug;
using Microsoft.VisualStudio.ProjectSystem.VS.Debug;

using PiDbg.Core;
using PiDbg.Infrastructure;

namespace PiDbg.Debug;

[Export(typeof(IDebugLaunchProvider))]
[AppliesTo(ProjectCapabilities.CSharp)]
[Order(int.MinValue)]
internal sealed class PiDbgDebugLaunchProvider : IDebugLaunchProvider
{
    // MIEngine GUID — handles vsdbg via DAP when DebuggerMIMode="vsdbg".
    private static readonly Guid MiEngineGuid =
        new("EA6637C6-17DF-45B5-A183-0951C54243BC");

    private readonly ConfiguredProject _project;

    [ImportingConstructor]
    public PiDbgDebugLaunchProvider(ConfiguredProject project)
    {
        _project = project;
    }

    public async Task<bool> CanLaunchAsync(DebugLaunchOptions launchOptions)
    {
        var profile = await PiDbgLaunchProfileWriter
            .TryReadAsync(_project.UnconfiguredProject.FullPath, CancellationToken.None)
            .ConfigureAwait(false);
        return profile.HasValue && !string.IsNullOrEmpty(profile.Value.Config.Host);
    }

    public Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsForDebugLaunchAsync(
        DebugLaunchOptions launchOptions)
        => QueryDebugTargetsAsync(launchOptions);

    public async Task<IReadOnlyList<IDebugLaunchSettings>> QueryDebugTargetsAsync(
        DebugLaunchOptions launchOptions)
    {
        _ = PiDbgPackage.Current
            ?? throw new InvalidOperationException("PiDbg package is not yet initialized.");

        var output      = PiDbgPackage.OutputWindow;
        var projectFile = _project.UnconfiguredProject.FullPath;

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        var profile = await PiDbgLaunchProfileWriter.TryReadAsync(projectFile, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Pi profile not found. Use 'Connect to Pi' to configure the connection.");

        if (string.IsNullOrEmpty(profile.Config.Host))
            throw new InvalidOperationException(
                "Pi host is not configured. Use 'Connect to Pi' to set up the connection.");

        var request = new SessionRequest
        {
            Connection  = profile.Config,
            AppName     = profile.AppName,
            ProjectPath = projectFile,
        };

        var progress = new Progress<string>(msg => output.WriteLine(OutputPane.PiDbg, msg));

        // Steps 1–6: SSH → provision → publish → deploy → start session → tunnel
        var info = await PiDbgPackage.Orchestrator
            .RunAsync(request, progress, ct)
            .ConfigureAwait(false);

        output.WriteLine(OutputPane.PiDbg, "Attaching VS debugger...");

        // Step 7 (VS-specific): hand off to MIEngine via TcpLaunchOptions
        var tcpOptions = $"""
            <TcpLaunchOptions xmlns="http://schemas.microsoft.com/vstudio/MDDDebuggerOptions/2014"
                Hostname="127.0.0.1"
                Port="{info.LocalPort}"
                ExePath="{info.AppDllPath}"
                TargetArchitecture="arm64"
                AdditionalSOLibSearchPath=""
                DebuggerMIMode="vsdbg">
                <AttachInfo>
                    <AttachInfoItem Name="ProcessId" Value="{info.AppPid}"/>
                </AttachInfo>
            </TcpLaunchOptions>
            """;

        var settings = new DebugLaunchSettings(launchOptions)
        {
            LaunchDebugEngineGuid = MiEngineGuid,
            LaunchOperation       = DebugLaunchOperation.CreateProcess,
            Options               = tcpOptions,
            Executable            = info.AppDllPath,
        };

        return new IDebugLaunchSettings[] { settings };
    }

    public async Task LaunchAsync(DebugLaunchOptions launchOptions)
    {
        await QueryDebugTargetsAsync(launchOptions).ConfigureAwait(false);
    }
}
