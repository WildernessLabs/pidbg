using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace PiDbg.Infrastructure;

public interface IGrpcChannelFactory
{
    Task<ChannelBase> GetOrCreateChannelAsync(SshSession session, CancellationToken ct);
    void DisposeChannel(string host);
}
