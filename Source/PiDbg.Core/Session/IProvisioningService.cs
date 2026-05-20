using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using PiDbg.Infrastructure;

namespace PiDbg.Core;

public interface IProvisioningService
{
    Task<ProvisioningOutcome> ProvisionAsync(
        SshSession      session,
        string          rootFolder,
        IProgress<string> progress,
        CancellationToken ct);
}

public sealed record ProvisioningOutcome
{
    public bool    Success     { get; init; }
    public string? Error       { get; init; }
    public ChannelBase? GrpcChannel { get; init; }
}
