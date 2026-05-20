using System;
using System.Threading;
using System.Threading.Tasks;
using PiDbg.Core;
using PiDbg.Infrastructure;

namespace PiDbg.Provisioning;

public sealed class ProvisioningService : IProvisioningService
{
    private readonly IGrpcChannelFactory _channelFactory;

    public ProvisioningService(IGrpcChannelFactory channelFactory)
    {
        _channelFactory = channelFactory;
    }

    public async Task<ProvisioningOutcome> ProvisionAsync(
        SshSession        session,
        string            rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var result = await ProvisioningOrchestrator
            .ProvisionAsync(session, _channelFactory, progress, rootFolder, ct)
            .ConfigureAwait(false);

        return new ProvisioningOutcome
        {
            Success     = result.Success,
            Error       = result.Error,
            GrpcChannel = result.Channel,
        };
    }
}
