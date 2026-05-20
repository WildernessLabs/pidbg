using System;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Core;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

namespace PiDbg.Provisioning;

// Adapts Core ProvisioningService for the VSIX context,
// bridging IOutputWindowService to IProgress<string>.
internal sealed class VsProvisioningService : IProvisioningService
{
    private readonly IGrpcChannelFactory  _channelFactory;
    private readonly IOutputWindowService _output;

    public VsProvisioningService(IGrpcChannelFactory channelFactory, IOutputWindowService output)
    {
        // Register the VSIX assembly so binary resources (daemon binary, vsdbg tarball)
        // are resolved from the VSIX rather than PiDbg.Core.
        ProvisioningResources.Register(typeof(VsProvisioningService).Assembly);

        _channelFactory = channelFactory;
        _output         = output;
    }

    public Task<ProvisioningOutcome> ProvisionAsync(
        SshSession        session,
        string            rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var bridged = new Progress<string>(msg => _output.WriteLine(OutputPane.PiDbg, msg));
        var svc     = new ProvisioningService(_channelFactory);
        return svc.ProvisionAsync(session, rootFolder, bridged, ct);
    }
}
