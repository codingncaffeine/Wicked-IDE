using System.Runtime.CompilerServices;
using System.Text;

namespace Wicked.Core.Chat;

/// <summary>
/// Reads a server-sent-events stream as it arrives: each event's <c>data</c> lines joined with newlines, yielded the moment its
/// blank line is read. Comment lines, <c>event:</c>, <c>id:</c> and <c>retry:</c> are skipped. Nothing is buffered beyond the line
/// being read, so a long reply is never held whole, and any split of the bytes (mid-line, mid-character) gives the same events.
/// </summary>
internal static class SseParser
{
    public static async IAsyncEnumerable<string> ReadDataAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
        var data = new StringBuilder();
        var any = false;
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0) yield return data.ToString(); // an event with no data is not dispatched
                data.Clear();
                any = false;
            }
            else if (line[0] == ':')
            {
                // A comment (keep-alive).
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var value = line.AsSpan(5);
                if (value.Length > 0 && value[0] == ' ') value = value[1..];
                if (any) data.Append('\n');
                data.Append(value);
                any = true;
            }
            else if (line == "data")
            {
                if (any) data.Append('\n');
                any = true;
            }
        }

        // The server closed without the last blank line: the event it was writing still counts.
        if (data.Length > 0) yield return data.ToString();
    }
}
