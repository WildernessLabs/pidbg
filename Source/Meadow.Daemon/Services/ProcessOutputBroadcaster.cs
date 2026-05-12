using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Meadow.Daemon.Contracts.V1;

namespace Meadow.Daemon.Services;

public sealed class ProcessOutputBroadcaster : IDisposable
{
    // Bounded: if no subscriber reads fast enough, oldest lines are dropped.
    private readonly Channel<OutputLine> _channel =
        Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(2000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,   // only ProcessManager writes
            SingleReader = false,  // multiple StreamOutput subscribers read
        });

    private readonly List<Channel<OutputLine>> _subscribers = new();
    private readonly SemaphoreSlim _subLock = new(1, 1);

    // Called by ProcessManager on each output line
    public bool TryWrite(OutputLine line)
    {
        // Write to primary channel
        _channel.Writer.TryWrite(line);
        
        // Fan out to all subscriber channels
        // Subscriber writes are non-blocking: slow subscribers drop items
        foreach (var sub in GetSubscribersSnapshot())
            sub.Writer.TryWrite(line);
            
        return true;
    }

    // Each gRPC StreamOutput call gets its own channel cursor
    public IAsyncEnumerable<OutputLine> Subscribe(CancellationToken ct)
    {
        var sub = Channel.CreateBounded<OutputLine>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
        AddSubscriber(sub);
        return WrapSubscriber(sub, ct);
    }

    private async IAsyncEnumerable<OutputLine> WrapSubscriber(
        Channel<OutputLine> sub,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            await foreach (var line in sub.Reader.ReadAllAsync(ct))
                yield return line;
        }
        finally
        {
            RemoveSubscriber(sub);
        }
    }

    private void AddSubscriber(Channel<OutputLine> sub)
    {
        _subLock.Wait();
        try { _subscribers.Add(sub); }
        finally { _subLock.Release(); }
    }

    private void RemoveSubscriber(Channel<OutputLine> sub)
    {
        // Use try-wait to avoid blocking if already disposed
        if (_subLock.Wait(0))
        {
            try { _subscribers.Remove(sub); }
            finally { _subLock.Release(); }
        }
    }

    private List<Channel<OutputLine>> GetSubscribersSnapshot()
    {
        _subLock.Wait();
        try { return _subscribers.ToList(); }
        finally { _subLock.Release(); }
    }

    public void Dispose()
    {
        _channel.Writer.TryComplete();
        _subLock.Wait();
        try
        {
            foreach (var sub in _subscribers)
                sub.Writer.TryComplete();
            _subscribers.Clear();
        }
        finally { _subLock.Release(); }
        _subLock.Dispose();
    }
}
