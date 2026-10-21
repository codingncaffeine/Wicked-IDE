using System.Diagnostics;
using System.Text;

namespace Wicked.App.Chat;

/// <summary>What the view shows of a reply at one moment.</summary>
/// <param name="Content">The answer, parsed.</param>
/// <param name="Reasoning">The model's thinking, parsed; null when it has none.</param>
/// <param name="Thinking">True while the thinking is under way (no answer text yet).</param>
/// <param name="ThoughtSeconds">How long the model has thought, or thought in all.</param>
public sealed record RenderFrame(IReadOnlyList<MdBlock> Content, IReadOnlyList<MdBlock>? Reasoning, bool Thinking, double ThoughtSeconds);

/// <summary>
/// Turns a reply's deltas into frames for the UI, off the UI thread. The stream's thread appends text as it arrives (a
/// lock and a flag); a worker of its own parses what has accumulated and delivers a <see cref="RenderFrame"/>, then
/// waits out the rest of the frame before the next. Deltas that arrive faster than that are coalesced into the one parse,
/// so the UI is handed at most one frame per refresh however fast the model writes.
/// </summary>
public sealed class StreamRenderer
{
    /// <summary>The spacing between frames: one refresh at 60 Hz.</summary>
    public static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    private readonly Lock _gate = new();
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly Action<RenderFrame> _deliver;
    private readonly StreamingMarkdown _contentParser = new();
    private readonly StreamingMarkdown _reasoningParser = new();
    private readonly Stopwatch _thought = new();
    private bool _thinkingStarted;
    private bool _answerStarted;
    private bool _complete;
    private int _pending;
    private Task? _loop;

    public StreamRenderer(Action<RenderFrame> deliver) => _deliver = deliver;

    /// <summary>Starts the worker; call once, before the first delta.</summary>
    public void Start() => _loop = Task.Run(Loop);

    public void AppendContent(string text)
    {
        lock (_gate)
        {
            if (!_answerStarted)
            {
                _answerStarted = true;
                _thought.Stop();
            }

            _content.Append(text);
            Mark();
        }
    }

    public void AppendReasoning(string text)
    {
        lock (_gate)
        {
            if (!_thinkingStarted)
            {
                _thinkingStarted = true;
                _thought.Start();
            }

            _reasoning.Append(text);
            Mark();
        }
    }

    private void Mark()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 0) _signal.Release();
    }

    /// <summary>Ends the stream: the last text is parsed and delivered as the final frame, which is returned.</summary>
    public async Task<RenderFrame> CompleteAsync()
    {
        lock (_gate)
        {
            _complete = true;
            _thought.Stop();
            if (Interlocked.Exchange(ref _pending, 1) == 0) _signal.Release();
        }

        if (_loop is not null) await _loop.ConfigureAwait(false);
        return _last ?? new RenderFrame([], null, false, 0);
    }

    private RenderFrame? _last;

    private async Task Loop()
    {
        var spacing = Stopwatch.StartNew();
        while (true)
        {
            await _signal.WaitAsync().ConfigureAwait(false);
            string content, reasoning;
            bool complete, thinking;
            double seconds;
            lock (_gate)
            {
                Interlocked.Exchange(ref _pending, 0);
                content = _content.ToString();
                reasoning = _reasoning.ToString();
                complete = _complete;
                seconds = _thought.Elapsed.TotalSeconds;
                thinking = _thinkingStarted && !_answerStarted && !complete;
            }

            var frame = new RenderFrame(
                _contentParser.Update(content),
                reasoning.Length == 0 ? null : _reasoningParser.Update(reasoning),
                thinking,
                seconds);
            _last = frame;
            if (complete) return;
            _deliver(frame);
            var wait = Frame - spacing.Elapsed;
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            spacing.Restart();
        }
    }
}
