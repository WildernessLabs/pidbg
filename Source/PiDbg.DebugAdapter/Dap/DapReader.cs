using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PiDbg.DebugAdapter.Dap;

// Reads length-prefixed DAP messages from a stream.
// Frame format: "Content-Length: N\r\n\r\n" followed by N UTF-8 bytes.
internal sealed class DapReader
{
    private readonly Stream _stream;

    public DapReader(Stream stream) => _stream = stream;

    public async Task<string?> ReadMessageAsync(CancellationToken ct)
    {
        // Read headers until blank line
        var headerBuilder = new StringBuilder();
        int contentLength  = -1;

        while (true)
        {
            var line = await ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;       // EOF
            if (line.Length == 0) break;         // blank line = end of headers

            if (line.StartsWith("Content-Length:", System.StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
        }

        if (contentLength <= 0) return null;

        var buf    = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await _stream.ReadAsync(buf, offset, contentLength - offset, ct).ConfigureAwait(false);
            if (read == 0) return null;
            offset += read;
        }

        return Encoding.UTF8.GetString(buf);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var oneByte = new byte[1];

        while (true)
        {
            var read = await _stream.ReadAsync(oneByte, 0, 1, ct).ConfigureAwait(false);
            if (read == 0) return sb.Length == 0 ? null : sb.ToString();

            var ch = (char)oneByte[0];
            if (ch == '\n') return sb.ToString().TrimEnd('\r');
            sb.Append(ch);
        }
    }
}
