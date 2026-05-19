using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.DebugAdapter.Dap;

// Writes length-prefixed DAP messages to a stream.
internal sealed class DapWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    public DapWriter(Stream stream) => _stream = stream;

    public async Task WriteMessageAsync(string json, CancellationToken ct)
    {
        var body   = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, 0, header.Length, ct).ConfigureAwait(false);
            await _stream.WriteAsync(body,   0, body.Length,   ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
