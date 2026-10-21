using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Contracts;
using Wicked.Core;

namespace Wicked.App.ViewModels;

/// <summary>One line of the request log.</summary>
public sealed class ApiLogRow(ApiLogEntry entry)
{
    public ApiLogEntry Entry { get; } = entry;

    public string Time => Entry.Time.LocalDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public string Request => $"{Entry.Method} {Entry.Route}";

    public string Model => Entry.Model ?? "";

    public string Status => Entry.Status.ToString(CultureInfo.InvariantCulture);

    public bool IsError => Entry.Status >= 400;

    public string Tokens => Entry.PromptTokens is null && Entry.CompletionTokens is null ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{Entry.PromptTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} in · {Entry.CompletionTokens?.ToString(CultureInfo.InvariantCulture) ?? "?"} out");

    public string Duration => Entry.Milliseconds < 1000 ? $"{Entry.Milliseconds} ms" : $"{Entry.Milliseconds / 1000.0:0.0} s";

    public string Client => Entry.Client ?? "";

    public string Note => Entry.Error ?? "";
}

/// <summary>
/// The request log view's model (D12): the daemon's recent requests and then each new one as it happens, read over the daemon's
/// socket on a worker and handed to the UI in batches. It follows the daemon across its restarts and says when there is none.
/// The shell mounts the view in its bottom panel and calls <see cref="Start"/> when it is shown and <see cref="Stop"/> when it is not.
/// </summary>
public sealed partial class ApiLogViewModel(WickedPaths paths) : ObservableObject, IDisposable
{
    private const int Kept = 1000;

    private readonly Lock _gate = new();
    private readonly List<ApiLogRow> _all = [];
    private CancellationTokenSource? _stop;

    /// <summary>The rows the filter lets through, newest first.</summary>
    public ObservableCollection<ApiLogRow> Rows { get; } = [];

    [ObservableProperty]
    private string _status = "Not following";

    [ObservableProperty]
    private bool _following;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>Begins following the daemon's log (once; calling it again does nothing).</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_stop is not null) return;
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _ = Task.Run(() => FollowAsync(token), CancellationToken.None);
        }

        Following = true;
        Status = "Looking for the daemon…";
    }

    public void Stop()
    {
        CancellationTokenSource? stop;
        lock (_gate)
        {
            stop = _stop;
            _stop = null;
        }

        stop?.Cancel();
        stop?.Dispose();
        Following = false;
        Status = "Not following";
    }

    [RelayCommand]
    private void Clear()
    {
        lock (_gate) _all.Clear();
        Rows.Clear();
        IsEmpty = true;
    }

    private async Task FollowAsync(CancellationToken ct)
    {
        long last = 0;
        var queue = Channel.CreateUnbounded<ApiLogEntry>();
        var pump = Task.Run(() => DeliverAsync(queue.Reader, ct), CancellationToken.None);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var client = new DaemonClient(paths.DaemonSocket, Timeout.InfiniteTimeSpan);
                    if (await client.GetHealthAsync(ct).ConfigureAwait(false) is null)
                    {
                        Say("The daemon is not running, so there is no request log.");
                    }
                    else
                    {
                        Say("Following the server's requests");
                        await foreach (var data in client.StreamAsync($"v1/wicked/log/stream?after={last.ToString(CultureInfo.InvariantCulture)}", null, ct).ConfigureAwait(false))
                        {
                            var entry = JsonSerializer.Deserialize(data, WickedJson.Default.ApiLogEntry)!;
                            last = Math.Max(last, entry.Id);
                            queue.Writer.TryWrite(entry);
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                {
                    Say("The daemon went away; looking for it again.");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        finally
        {
            queue.Writer.TryComplete();
            await pump.ConfigureAwait(false);
        }
    }

    private void Say(string text) => Dispatcher.UIThread.Post(() =>
    {
        if (Following) Status = text;
    });

    /// <summary>Hands entries to the UI thread a batch at a time, a tenth of a second apart at most, so a busy server does not flood it.</summary>
    private async Task DeliverAsync(ChannelReader<ApiLogEntry> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                var batch = new List<ApiLogEntry>();
                while (reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count > 0) Dispatcher.UIThread.Post(() => Add(batch));
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private void Add(List<ApiLogEntry> batch)
    {
        lock (_gate)
        {
            foreach (var entry in batch.Where(e => _all.All(r => r.Entry.Id != e.Id))) _all.Add(new ApiLogRow(entry));
            if (_all.Count > Kept) _all.RemoveRange(0, _all.Count - Kept);
        }

        Rebuild();
    }

    private void Rebuild()
    {
        List<ApiLogRow> shown;
        lock (_gate)
        {
            shown = [.. _all.Where(Matches).Reverse()];
        }

        Rows.Clear();
        foreach (var row in shown) Rows.Add(row);
        IsEmpty = Rows.Count == 0;
    }

    private bool Matches(ApiLogRow row) =>
        Filter.Length == 0 || row.Request.Contains(Filter, StringComparison.OrdinalIgnoreCase) || row.Model.Contains(Filter, StringComparison.OrdinalIgnoreCase)
        || row.Status == Filter || row.Client.Contains(Filter, StringComparison.OrdinalIgnoreCase);

    public void Dispose() => Stop();
}
