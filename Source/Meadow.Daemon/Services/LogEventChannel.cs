using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

public sealed class LogEventChannel : IAsyncDisposable
{
    // Bounded at 10,000 — if no subscriber is reading, events drop rather than OOM.
    private readonly Channel<LogEvent> _channel =
        Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = false,
            SingleReader = false
        });

    public bool TryWrite(LogEvent evt) => _channel.Writer.TryWrite(evt);

    // Each call to Subscribe returns an independent async enumerable.
    // Multiple gRPC stream calls each get their own cursor.
    public IAsyncEnumerable<LogEvent> Subscribe(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        // Drain remaining items so subscribers see a clean end
        try
        {
            await foreach (var _ in _channel.Reader.ReadAllAsync()) { }
        }
        catch (OperationCanceledException) { }
    }
}
