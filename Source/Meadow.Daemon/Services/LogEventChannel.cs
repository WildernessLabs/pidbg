using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

// Phase 2 will upgrade this to a proper fan-out broadcast (list of inner channels).
// For phase 1: single bounded channel sufficient for the skeleton.
internal sealed class LogEventChannel
{
    private readonly List<Channel<LogEvent>> _subscribers = [];
    private readonly object _lock = new();

    public void Publish(LogEvent entry)
    {
        lock (_lock)
        {
            foreach (var ch in _subscribers)
                ch.Writer.TryWrite(entry);
        }
    }

    public async IAsyncEnumerable<LogEvent> Subscribe(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(512)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true,
        });

        lock (_lock) _subscribers.Add(ch);
        try
        {
            await foreach (var entry in ch.Reader.ReadAllAsync(ct))
                yield return entry;
        }
        finally
        {
            lock (_lock) _subscribers.Remove(ch);
        }
    }
}
