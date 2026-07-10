using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PiDbg.Build;
using PiDbg.Core;
using PiDbg.DebugAdapter;
using PiDbg.DebugAdapter.Dap;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

// Redirect Console.Out to stderr so DAP messages on stdout are not polluted.
Console.SetOut(Console.Error);

// Register this assembly as the source for binary embedded resources (daemon binary, vsdbg tarball).
ProvisioningResources.Register(System.Reflection.Assembly.GetExecutingAssembly());

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var logFactory = LoggerFactory.Create(b =>
    b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; })
     .SetMinimumLevel(LogLevel.Information));

var stdin  = Console.OpenStandardInput();
var stdout = Console.OpenStandardOutput();
var reader = new DapReader(stdin);
var writer = new DapWriter(stdout);

var sshLogger     = logFactory.CreateLogger<SshConnectionManager>();
var cliLogger     = logFactory.CreateLogger<CliPublishRunner>();
var provLogger    = logFactory.CreateLogger<CliProvisioningService>();
var orchLogger    = logFactory.CreateLogger<SessionOrchestrator>();
var adapterLogger = logFactory.CreateLogger<PiDbgDebugAdapter>();

var ssh          = new SshConnectionManager(sshLogger);
var grpcChannels = new NetGrpcChannelFactory();
var tunnels      = new DebugTunnelManager();
var provisioning = new CliProvisioningService(grpcChannels, provLogger);
var publisher    = new CliPublishRunner(cliLogger);

var orchestrator = new SessionOrchestrator(
    ssh, grpcChannels, tunnels,
    publisher, provisioning, orchLogger);

var adapter = new PiDbgDebugAdapter(reader, writer, orchestrator, adapterLogger);

try
{
    var (info, _) = await adapter.RunHandshakeAsync(cts.Token).ConfigureAwait(false);

    if (info is not null)
    {
        ProgramLog.ProxyStarted(logFactory.CreateLogger("DapProxy"), info.LocalPort);

        using var proxy = await DapProxy.ConnectAsync(
            reader, writer,
            "127.0.0.1", info.LocalPort,
            logFactory.CreateLogger<DapProxy>(),
            cts.Token).ConfigureAwait(false);

        // Initialize vsdbg and attach to the app process.
        // vsdbg will send its own "initialized" event, which the handshake forwards to VS Code.
        // VS Code reacts with setBreakpoints/configurationDone, which the proxy loop below handles.
        await proxy.HandshakeWithVsdbgAsync(info.AppPid, cts.Token).ConfigureAwait(false);

        await proxy.RunAsync(
            onDisconnect: () =>
            {
                // Teardown: tunnels are closed in the finally block below
                tunnels.CloseAllTunnels();
                return Task.CompletedTask;
            },
            onConfigurationDone: () => info.ResumeAsync(cts.Token),
            cts.Token).ConfigureAwait(false);
    }
}
catch (OperationCanceledException) { /* clean exit */ }
catch (Exception ex)
{
    ProgramLog.AdapterCrashed(logFactory.CreateLogger("Program"), ex);
    return 1;
}
finally
{
    (tunnels   as IDisposable)?.Dispose();
    (grpcChannels as IDisposable)?.Dispose();
    (ssh       as IDisposable)?.Dispose();
}

return 0;
