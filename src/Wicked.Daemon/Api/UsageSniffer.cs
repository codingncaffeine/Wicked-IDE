using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Daemon.Api;

/// <summary>
/// Reads the token counts out of a reply as it passes by, without keeping it: the first and the last few kilobytes are held (a
/// chat reply names its usage in its last event, Anthropic's input count in its first). Works for JSON replies and for event
/// streams alike, whatever the dialect: <c>prompt_tokens</c>/<c>completion_tokens</c> (OpenAI), <c>input_tokens</c>/<c>output_tokens</c>
/// (Anthropic and Responses).
/// </summary>
public sealed partial class UsageSniffer
{
    private const int Head = 4096;
    private const int Tail = 8192;

    private readonly byte[] _head = new byte[Head];
    private readonly byte[] _tail = new byte[Tail];
    private int _headLength;
    private long _total;

    [GeneratedRegex("\"(?:prompt_tokens|input_tokens)\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Prompt();

    [GeneratedRegex("\"(?:completion_tokens|output_tokens)\"\\s*:\\s*(\\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex Completion();

    public long Bytes => _total;

    public void Add(ReadOnlySpan<byte> chunk)
    {
        if (_headLength < Head)
        {
            var take = Math.Min(Head - _headLength, chunk.Length);
            chunk[..take].CopyTo(_head.AsSpan(_headLength));
            _headLength += take;
        }

        // The tail is a sliding window: keep the last Tail bytes seen.
        if (chunk.Length >= Tail)
        {
            chunk[^Tail..].CopyTo(_tail);
        }
        else
        {
            var kept = (int)Math.Min(_total, Tail);
            var keep = Math.Min(kept, Tail - chunk.Length);
            if (keep > 0) Buffer.BlockCopy(_tail, kept - keep, _tail, 0, keep);
            chunk.CopyTo(_tail.AsSpan(keep));
        }

        _total += chunk.Length;
    }

    /// <summary>The counts found, each null when the reply never said.</summary>
    public (int? Prompt, int? Completion) Result()
    {
        var tail = Encoding.UTF8.GetString(_tail, 0, (int)Math.Min(_total, Tail));
        var head = Encoding.UTF8.GetString(_head, 0, _headLength);
        return (LastNumber(Prompt(), tail) ?? LastNumber(Prompt(), head), LastNumber(Completion(), tail) ?? LastNumber(Completion(), head));
    }

    private static int? LastNumber(Regex pattern, string text)
    {
        Match? last = null;
        foreach (Match m in pattern.Matches(text)) last = m;
        return last is not null && int.TryParse(last.Groups[1].Value, out var n) ? n : null;
    }
}
