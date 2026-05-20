using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using PiDbg.Core;
using PiDbg.Infrastructure;
using PiDbg.Provisioning;

namespace PiDbg.DebugAdapter;

internal sealed partial class CliProvisioningService : IProvisioningService
{
    private readonly IGrpcChannelFactory             _channelFactory;
    private readonly ILogger<CliProvisioningService> _logger;

    public CliProvisioningService(IGrpcChannelFactory channelFactory, ILogger<CliProvisioningService> logger)
    {
        _channelFactory = channelFactory;
        _logger         = logger;
    }

    public async Task<ProvisioningOutcome> ProvisionAsync(
        SshSession        session,
        string            rootFolder,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var svc = new ProvisioningService(_channelFactory);
        try
        {
            return await svc.ProvisionAsync(session, rootFolder, progress, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProvisioningFailed(_logger, ex);
            return new ProvisioningOutcome
            {
                Success = false,
                Error   = $"Provisioning failed: {ex.Message}",
            };
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Provisioning failed with unhandled exception")]
    private static partial void LogProvisioningFailed(ILogger logger, Exception ex);
}
