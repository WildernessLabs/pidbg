using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

// Fan-out log channel: each subscriber gets its own bounded inner channel so that
// slow or disconnected gRPC clients don't starve other subscribers.
public sealed class LogEventChannel : IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly List<Channel<LogEvent>> _subscribers = [];
    private bool _disposed;

    public bool TryWrite(LogEvent evt)
    {
        List<Channel<LogEvent>> snapshot;
        lock (_lock)
        {
            if (_disposed) return false;
            snapshot = [.._subscribers];
        }
        foreach (var ch in snapshot)
            ch.Writer.TryWrite(evt);
        return true;
    }

    public IAsyncEnumerable<LogEvent> Subscribe(CancellationToken ct)
    {
        var ch = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        lock (_lock)
        {
            if (_disposed)
            {
                ch.Writer.TryComplete();
                return ch.Reader.ReadAllAsync(ct);
            }
            _subscribers.Add(ch);
        }

        return ReadAndUnsubscribeAsync(ch, ct);
    }

    private async IAsyncEnumerable<LogEvent> ReadAndUnsubscribeAsync(
        Channel<LogEvent> ch,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            lock (_lock)
                _subscribers.Remove(ch);
            ch.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lock)
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            foreach (var ch in _subscribers)
                ch.Writer.TryComplete();
            _subscribers.Clear();
        }
        return ValueTask.CompletedTask;
    }
}
