using System;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Core;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

// Adapts the VS-specific ProvisioningOrchestrator to the IDE-neutral IProvisioningService.
internal sealed class VsProvisioningService : IProvisioningService
{
    private readonly IGrpcChannelFactory  _channelFactory;
    private readonly IOutputWindowService _output;

    public VsProvisioningService(IGrpcChannelFactory channelFactory, IOutputWindowService output)
    {
        _channelFactory = channelFactory;
        _output         = output;
    }

    public async Task<ProvisioningOutcome> ProvisionAsync(
        SshSession        session,
        string            rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var result = await ProvisioningOrchestrator
            .ProvisionAsync(session, _channelFactory, _output, rootFolder, ct)
            .ConfigureAwait(false);

        return new ProvisioningOutcome
        {
            Success     = result.Success,
            Error       = result.Error,
            GrpcChannel = result.Channel,
        };
    }
}
