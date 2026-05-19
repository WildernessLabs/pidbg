using System;
using System.Threading;
using System.Threading.Tasks;
using Meadow.Daemon.Contracts.V1;
using Microsoft.Extensions.Logging;
using PiDbg.Core;
using PiDbg.Infrastructure;

namespace PiDbg.DebugAdapter;

// Lightweight provisioning for the CLI/adapter context:
// assumes the device was already provisioned via the VSIX.
// Opens the gRPC channel and verifies the daemon responds.
internal sealed class CliProvisioningService : IProvisioningService
{
    private readonly IGrpcChannelFactory _channelFactory;
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
        progress.Report("Connecting to daemon...");

        Grpc.Core.Channel channel;
        try
        {
            channel = await _channelFactory
                .GetOrCreateChannelAsync(session, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open gRPC channel");
            return new ProvisioningOutcome
            {
                Success = false,
                Error   = $"Cannot connect to PiDbg daemon on {session.Host}. " +
                          $"Ensure the device is provisioned via the PiDbg Visual Studio extension. " +
                          $"Detail: {ex.Message}",
            };
        }

        progress.Report("Verifying daemon health...");
        try
        {
            var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);
            await client.PingAsync(
                new PingRequest(),
                deadline: DateTime.UtcNow.AddSeconds(10),
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Daemon health check failed");
            return new ProvisioningOutcome
            {
                Success = false,
                Error   = $"PiDbg daemon on {session.Host} is not responding. " +
                          $"Ensure the device is provisioned via the PiDbg Visual Studio extension. " +
                          $"Detail: {ex.Message}",
            };
        }

        progress.Report("Daemon ready.");
        return new ProvisioningOutcome { Success = true, GrpcChannel = channel };
    }
}
