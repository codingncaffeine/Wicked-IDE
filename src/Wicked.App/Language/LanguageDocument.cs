using System.Threading.Channels;
using Wicked.Core;
using Wicked.Core.Lsp;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.App.Language;

/// <summary>
/// One open document as its language server sees it. Everything said to the server about it goes through one queue on a
/// worker, in order: open, each change, save, close, and the requests, so a request is always about the text the server
/// has. A request is sent from the queue but not awaited there, so a slow answer never holds up the next keystroke.
/// The texts sent lately are kept by version, so an answer computed for an older version can still be placed.
/// </summary>
public sealed class LanguageDocument : IAsyncDisposable
{
    private const int KeptVersions = 64;

    private readonly Channel<Action<LanguageClient>> _queue = Channel.CreateUnbounded<Action<LanguageClient>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<long, Rope> _sent = [];
    private readonly Queue<long> _sentOrder = new();
    private readonly object _gate = new();
    private readonly Task _pump;
    private LanguageClient? _client;
    private TaskCompletionSource<LanguageClient?> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LanguageDocument(Document document, string path, string languageId)
    {
        Document = document;
        Path = path;
        Uri = LspPaths.ToUri(path);
        LanguageId = languageId;
        _pump = Task.Run(PumpAsync);
    }

    public Document Document { get; }

    public string Path { get; }

    public string Uri { get; }

    public string LanguageId { get; }

    /// <summary>How many tabs show it (a split shows one document twice); it closes in the server when none does.</summary>
    public int Tabs { get; set; }

    /// <summary>The server, once it is running and has the document; null while it starts or when there is none.</summary>
    public LanguageClient? Client => Volatile.Read(ref _client);

    /// <summary>The server once it has the document, or null when there is none.</summary>
    public Task<LanguageClient?> Ready => _ready.Task;

    /// <summary>Gives the document to a (new) server: opened with its current text. UI thread (it reads the text).</summary>
    public void Bind(LanguageClient? client)
    {
        var text = Document.Text;
        var version = Document.Version;
        if (client is null)
        {
            Volatile.Write(ref _client, null);
            _ready.TrySetResult(null);
            return;
        }

        if (_ready.Task.IsCompleted) _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Remember(version, text);
        Volatile.Write(ref _client, client);
        var ready = _ready;
        _queue.Writer.TryWrite(c =>
        {
            c.Open(Uri, LanguageId, (int)version, text.ToString());
            ready.TrySetResult(c);
        });
    }

    /// <summary>An edit, in order with everything else. UI thread.</summary>
    public void Changed(DocumentChange change)
    {
        Remember(change.Version, change.After);
        if (Client is null) return;
        _queue.Writer.TryWrite(c => c.Change(Uri, (int)change.Version, LspText.ChangesOf(change), () => change.After.ToString()));
    }

    public void Saved()
    {
        var text = Document.Text;
        if (Client is not null) _queue.Writer.TryWrite(c => c.Save(Uri, text.ToString()));
    }

    /// <summary>
    /// A request about the document as it is now: sent after every edit made so far, with the text and version it is
    /// about, so the answer's positions can be read against that text.
    /// </summary>
    public Task<T?> RequestAsync<T>(Func<LanguageClient, Rope, long, Task<T>> request, CancellationToken ct = default)
    {
        var text = Document.Text;
        var version = Document.Version;
        var answer = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Client is null)
        {
            answer.SetResult(default);
            return answer.Task;
        }

        ct.Register(() => answer.TrySetCanceled(ct));
        _queue.Writer.TryWrite(c =>
        {
            if (ct.IsCancellationRequested) return;
            Task<T> sent;
            try
            {
                sent = request(c, text, version);
            }
            catch (Exception ex)
            {
                answer.TrySetException(ex);
                return;
            }

            sent.ContinueWith(t =>
            {
                if (t.IsCanceled) answer.TrySetCanceled();
                else if (t.IsFaulted) answer.TrySetException(t.Exception!.GetBaseException());
                else answer.TrySetResult(t.Result);
            }, TaskScheduler.Default);
        });
        return answer.Task;
    }

    /// <summary>The text that was sent as <paramref name="version"/>, when it is still kept.</summary>
    public Rope? TextAt(long version)
    {
        lock (_gate) return _sent.TryGetValue(version, out var text) ? text : null;
    }

    /// <summary>The newest version sent and its text.</summary>
    public (long Version, Rope Text) Latest
    {
        get
        {
            lock (_gate)
            {
                var v = _sentOrder.Count == 0 ? Document.Version : _sentOrder.Max();
                return (v, _sent.TryGetValue(v, out var t) ? t : Document.Text);
            }
        }
    }

    private void Remember(long version, Rope text)
    {
        lock (_gate)
        {
            if (_sent.TryAdd(version, text)) _sentOrder.Enqueue(version);
            while (_sentOrder.Count > KeptVersions) _sent.Remove(_sentOrder.Dequeue());
        }
    }

    private async Task PumpAsync()
    {
        await foreach (var step in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Client is not { IsRunning: true } client) continue;
            try
            {
                step(client);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                Log.Warn($"Language server message for {System.IO.Path.GetFileName(Path)} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Closes the document in its server and stops the queue.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Client is not null) _queue.Writer.TryWrite(c => c.Close(Uri));
        _queue.Writer.TryComplete();
        _ready.TrySetResult(null);
        await _pump.ConfigureAwait(false);
    }
}
