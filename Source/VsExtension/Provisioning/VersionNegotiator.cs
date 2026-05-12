using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Meadow.Daemon.Contracts.V1;

namespace PiDbg.Provisioning;

internal static class VersionNegotiator
{
    private static readonly VersionManifest Manifest = VersionManifest.Load();

    public static async Task<NegotiationResult> NegotiateAsync(Channel channel, CancellationToken ct)
    {
        var client = new MeadowDaemonService.MeadowDaemonServiceClient(channel);

        PongResponse pong;
        try
        {
            pong = await client.PingAsync(new PingRequest(), cancellationToken: ct)
                               .ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            return new NegotiationResult(false, 0, false,
                $"Could not ping daemon: {ex.Status.Detail}");
        }

        var protoVer = pong.Version?.ProtocolVersion ?? 0;
        if (protoVer < Manifest.MinProtoVersion)
            return new NegotiationResult(false, protoVer, false,
                $"Daemon proto version {protoVer} is too old. " +
                $"This VSIX requires proto version {Manifest.MinProtoVersion}+. " +
                "Update the daemon via PiDbg: Repair Connection.");

        var daemonVer      = pong.Version?.Version ?? "";
        var upgradeNeeded  = IsVersionLessThan(daemonVer, Manifest.PreferredDaemon);

        return new NegotiationResult(true, protoVer, upgradeNeeded, null);
    }

    private static bool IsVersionLessThan(string installed, string required)
    {
        if (string.IsNullOrEmpty(installed) || string.IsNullOrEmpty(required)) return false;
        if (Version.TryParse(installed, out var v1) && Version.TryParse(required, out var v2))
            return v1 < v2;
        return string.Compare(installed, required, StringComparison.OrdinalIgnoreCase) < 0;
    }
}
