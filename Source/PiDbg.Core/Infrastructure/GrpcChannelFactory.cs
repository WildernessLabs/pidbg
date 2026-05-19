using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Renci.SshNet;

namespace PiDbg.Infrastructure;

public sealed class GrpcChannelFactory : IGrpcChannelFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, (Channel Channel, ForwardedPortLocal Tunnel)>
        _channels = new ConcurrentDictionary<string, (Channel, ForwardedPortLocal)>(
            StringComparer.OrdinalIgnoreCase);

    public async Task<Channel> GetOrCreateChannelAsync(SshSession session, CancellationToken ct)
    {
        if (_channels.TryGetValue(session.Host, out var existing) && existing.Tunnel.IsStarted)
            return existing.Channel;

        var (tunnel, localPort) = await session.OpenTunnelAsync(50051, ct).ConfigureAwait(false);

        var channel = new Channel("127.0.0.1", localPort, ChannelCredentials.Insecure,
            new[]
            {
                new ChannelOption(ChannelOptions.MaxReceiveMessageLength, 64 * 1024 * 1024),
                new ChannelOption(ChannelOptions.MaxSendMessageLength,    64 * 1024 * 1024),
            });

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
