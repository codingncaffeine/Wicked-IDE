using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Wicked.Core.Terminal;

/// <summary>What a session is started with.</summary>
public sealed record TerminalSessionOptions
{
    public required PtyOptions Launch { get; init; }

    public TerminalOptions Terminal { get; init; } = new();

    /// <summary>A program that stops the user's shell from drawing (a frame goes out at most this often); 16 ms is 60 frames a second.</summary>
    public TimeSpan MinFrameInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>Starts the pty. Replaced by tests that feed the emulator without a process.</summary>
    public Func<PtyOptions, PtyDataHandler, IPty> StartPty { get; init; } = Pty.Start;
}

/// <summary>
/// A shell on a pty and the terminal showing it. Output is read on the pty's thread, handed to the session's emulator
/// thread through a bounded queue (a program that writes faster than the terminal reads is held back), and parsed there;
/// finished frames are handed out through <see cref="FrameReady"/>. Everything that touches the emulator's state is a
/// queued job on that one thread, so nothing is locked and the UI thread never waits for it.
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private const int MaxChunksInFlight = 32;

    private readonly TerminalSessionOptions _options;
    private readonly TerminalEmulator _emulator;
    private readonly ConcurrentQueue<Work> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly SemaphoreSlim _credits = new(MaxChunksInFlight);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private IPty? _pty;
    private bool _dirty;
    private TimeSpan _lastFrame = TimeSpan.FromSeconds(-1);
    private TimeSpan _syncStarted;
    private (int Columns, int Rows)? _pendingResize;
    private int _disposed;
    private long _bytesProcessed;
    private int _exited;

    private readonly record struct Work(byte[]? Data, int Length, Action? Job);

    public TerminalSession(TerminalSessionOptions options)
    {
        _options = options;
        _emulator = new TerminalEmulator(options.Launch.Columns, options.Launch.Rows, options.Terminal);
        Columns = options.Launch.Columns;
        Rows = options.Launch.Rows;
        _emulator.Reply += bytes => _pty?.Write(bytes.Span);
        _emulator.TitleChanged += title => TitleChanged?.Invoke(title);
        _emulator.Bell += () => Bell?.Invoke();
        _emulator.WorkingDirectoryChanged += dir => WorkingDirectoryChanged?.Invoke(dir);
        _emulator.ClipboardWrite += text => ClipboardWrite?.Invoke(text);
        _emulator.CommandStarted += record => CommandStarted?.Invoke(record);
        _emulator.CommandFinished += record => CommandFinished?.Invoke(record);
        _emulator.PromptStarted += record => PromptStarted?.Invoke(record);
        _emulator.ModesChanged += modes => ModesChanged?.Invoke(modes);
        _emulator.ProgressChanged += (state, value) => ProgressChanged?.Invoke(state, value);
        _thread = new Thread(Run) { IsBackground = true, Name = "terminal emulator" };
    }

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int ProcessId => _pty?.ProcessId ?? 0;

    /// <summary>The terminal's modes as of the latest output (safe to read from any thread).</summary>
    public TerminalModes Modes => _emulator.Modes;

    /// <summary>The exit code once the program ended; null while it runs.</summary>
    public int? ExitCode { get; private set; }

    /// <summary>How many bytes of output the emulator has taken in.</summary>
    public long BytesProcessed => Interlocked.Read(ref _bytesProcessed);

    /// <summary>A finished frame, on the emulator's thread. Take it and post it to wherever it is shown.</summary>
    public event Action<TerminalFrame>? FrameReady;

    public event Action<int>? Exited;

    public event Action<string>? TitleChanged;

    public event Action? Bell;

    public event Action<string>? WorkingDirectoryChanged;

    public event Action<string>? ClipboardWrite;

    public event Action<CommandRecord>? PromptStarted;

    public event Action<CommandRecord>? CommandStarted;

    public event Action<CommandRecord>? CommandFinished;

    public event Action<TerminalModes>? ModesChanged;

    public event Action<int, int>? ProgressChanged;

    /// <summary>Starts the program and the emulator thread. Spawning a process takes a moment: call it off the UI thread.</summary>
    public void Start()
    {
        _thread.Start();
        _pty = _options.StartPty(_options.Launch, OnData);
        _ = _pty.Exited.ContinueWith(task => Enqueue(() => Finish(task.IsCompletedSuccessfully ? task.Result : -1)), TaskScheduler.Default);
    }

    private void OnData(byte[] rented, int length)
    {
        // Blocks the pty's reader while the emulator is behind: the program's writes then wait for the terminal.
        try
        {
            _credits.Wait(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            ArrayPool<byte>.Shared.Return(rented);
            return;
        }

        _queue.Enqueue(new Work(rented, length, null));
        _signal.Release();
    }

    private void Enqueue(Action job)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _queue.Enqueue(new Work(null, 0, job));
        _signal.Release();
    }

    // ── Input ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sends bytes to the program, as typed. Never blocks.</summary>
    public void Input(ReadOnlySpan<byte> data) => _pty?.Write(data);

    public void Input(string text) => Input(Encoding.UTF8.GetBytes(text));

    /// <summary>Pastes text: with bracketed paste on the program is told it is a paste, and line breaks become what Enter sends.</summary>
    public void Paste(string text)
    {
        if (text.Length == 0) return;
        var clean = text.ReplaceLineEndings("\r");
        if (Modes.BracketedPaste)
        {
            // The end marker inside the text would let it escape the paste.
            clean = clean.Replace("\u001b[201~", "[201~", StringComparison.Ordinal);
            clean = "\u001b[200~" + clean + "\u001b[201~";
        }

        Input(clean);
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        Columns = columns;
        Rows = rows;
        Enqueue(() => _pendingResize = (columns, rows));
    }

    public void ReportFocus(bool focused)
    {
        if (Modes.FocusReporting) Input(focused ? "\u001b[I" : "\u001b[O");
    }

    public void Kill() => _pty?.Kill();

    // ── Jobs on the emulator's thread ────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="job"/> on the emulator's thread and returns its result; the job may touch the emulator.</summary>
    public Task<T> RunAsync<T>(Func<TerminalEmulator, T> job)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Volatile.Read(ref _disposed) != 0)
        {
            done.SetCanceled();
            return done.Task;
        }

        Enqueue(() =>
        {
            try
            {
                done.SetResult(job(_emulator));
                _dirty = true;
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        return done.Task;
    }

    /// <summary>Like <see cref="RunAsync{T}"/> for a job with no result.</summary>
    public Task RunAsync(Action<TerminalEmulator> job) => RunAsync<object?>(e =>
    {
        job(e);
        return null;
    });

    public void ScrollViewport(int lines) => Enqueue(() =>
    {
        _emulator.ScrollViewport(lines);
        _dirty = true;
    });

    public void ScrollViewportToBottom() => Enqueue(() =>
    {
        _emulator.ScrollViewportToBottom();
        _dirty = true;
    });

    public void ScrollViewportToLine(long line) => Enqueue(() =>
    {
        _emulator.ScrollViewportToLine(line);
        _dirty = true;
    });

    /// <summary>Clears the scrollback and the screen, as the Clear command does (the shell redraws its prompt when asked to).</summary>
    public void Clear() => Enqueue(() =>
    {
        _emulator.ClearScrollback();
        _emulator.Write("\u001b[2J\u001b[H"u8);
        _dirty = true;
        _pty?.Write("\f"u8);
    });

    public Task<string> GetTextAsync(TerminalSelection selection) => RunAsync(e => e.GetText(selection));

    public Task<List<TerminalCommand>> GetCommandsAsync() => RunAsync(e => e.Commands());

    /// <summary>Searches the whole buffer; the search itself runs on the thread pool, off both the emulator and the UI thread.</summary>
    public async Task<(int Epoch, List<TerminalMatch> Matches)> SearchAsync(string query, SearchOptions options, CancellationToken ct)
    {
        var snapshot = await RunAsync(e => e.SnapshotForSearch()).ConfigureAwait(false);
        var matches = await Task.Run(() => snapshot.Find(query, options, ct), ct).ConfigureAwait(false);
        return (snapshot.Epoch, matches);
    }

    // ── The emulator's thread ────────────────────────────────────────────────────────────────────

    private void Run()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var wait = NextFrameDelay();
                if (!_signal.Wait(wait, _stop.Token))
                {
                    PublishIfDue();
                    continue;
                }

                // Take everything that is waiting in one go: one frame covers all of it.
                do
                {
                    if (!_queue.TryDequeue(out var work)) break;
                    Process(work);
                    ApplyResize();
                    PublishIfDue();
                }
                while (_signal.Wait(0));

                ApplyResize();
                PublishIfDue();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Log.Error($"Terminal emulator stopped: {e}");
        }
    }

    private TimeSpan NextFrameDelay()
    {
        if (!_dirty) return Timeout.InfiniteTimeSpan;
        var due = _lastFrame + _options.MinFrameInterval - _clock.Elapsed;
        return due <= TimeSpan.Zero ? TimeSpan.Zero : due;
    }

    private void Process(Work work)
    {
        if (work.Data is { } data)
        {
            try
            {
                _emulator.Write(data.AsSpan(0, work.Length));
                Interlocked.Add(ref _bytesProcessed, work.Length);
                _dirty = true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                _credits.Release();
            }

            return;
        }

        try
        {
            work.Job!();
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal job failed: {e.Message}");
        }
    }

    private void ApplyResize()
    {
        if (_pendingResize is not { } size) return;
        _pendingResize = null;
        if (size.Columns == _emulator.Columns && size.Rows == _emulator.Rows) return;
        _emulator.Resize(size.Columns, size.Rows);
        _pty?.Resize(size.Columns, size.Rows);
        _dirty = true;
    }

    private void PublishIfDue()
    {
        if (!_dirty) return;
        var now = _clock.Elapsed;
        if (now - _lastFrame < _options.MinFrameInterval) return;

        // A program that brackets its redraw in synchronized output is not shown half-drawn, unless it forgets to end it.
        if (_emulator.Modes.SynchronizedOutput)
        {
            if (_syncStarted == TimeSpan.Zero) _syncStarted = now;
            if (now - _syncStarted < TimeSpan.FromMilliseconds(1000)) return;
        }
        else _syncStarted = TimeSpan.Zero;

        _dirty = false;
        _lastFrame = now;
        FrameReady?.Invoke(_emulator.BuildFrame());
    }

    private void Finish(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) != 0) return;
        ExitCode = code;
        Exited?.Invoke(code);
        _dirty = true;
    }

    /// <summary>Writes text into the terminal itself, as if the program had printed it (the exit notice uses it).</summary>
    public void Print(string text) => Enqueue(() =>
    {
        _emulator.Write(Encoding.UTF8.GetBytes(text));
        _dirty = true;
    });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _pty?.Dispose();
        _stop.Cancel();
        // Wake the emulator thread so it sees the stop.
        _signal.Release();
    }
}
