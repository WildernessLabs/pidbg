using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Renci.SshNet;

namespace PiDbg.Infrastructure;

public sealed class NetGrpcChannelFactory : IGrpcChannelFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, (ChannelBase Channel, ForwardedPortLocal Tunnel)>
        _channels = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ChannelBase> GetOrCreateChannelAsync(SshSession session, CancellationToken ct)
    {
        if (_channels.TryGetValue(session.Host, out var existing) && existing.Tunnel.IsStarted)
            return existing.Channel;

        var (tunnel, localPort) = await session.OpenTunnelAsync(50051, ct).ConfigureAwait(false);

        var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{localPort}");

        _channels[session.Host] = (channel, tunnel);
        return channel;
    }

    public void DisposeChannel(string host)
    {
        if (_channels.TryRemove(host, out var entry))
        {
            entry.Tunnel.Stop();
            _ = entry.Channel.ShutdownAsync();
        }
    }

    public void Dispose()
    {
        foreach (var entry in _channels.Values)
        {
            entry.Tunnel.Stop();
            _ = entry.Channel.ShutdownAsync();
        }
        _channels.Clear();
    }
}
