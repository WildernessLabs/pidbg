using Grpc.Core;
using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.Infrastructure;

public interface IGrpcChannelFactory
{
    Task<Channel> GetOrCreateChannelAsync(SshSession session, CancellationToken ct);
    void DisposeChannel(string host);
}
