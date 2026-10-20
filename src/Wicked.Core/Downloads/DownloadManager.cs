using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Wicked.Core.Downloads;

/// <summary>
/// The download manager (D5): a queue of file jobs that resume, verify and survive a restart. Each job streams into
/// <c>&lt;file&gt;.part</c> beside a <c>.part.json</c> sidecar holding the validators (URL, ETag, size); a retry or a restart
/// sends <c>Range</c> + <c>If-Range</c>, so a changed file restarts from zero instead of being stitched. The sha256 is
/// computed while writing (the existing part is re-hashed on resume), compared at the end, and only then is the part
/// renamed over the destination, so a file at its final name is always complete and verified. Nothing here blocks a
/// caller: control methods return at once or are async, work runs on the thread pool, and <see cref="Changed"/> is raised
/// from whichever thread did the work, at most ten progress events a second per job.
/// </summary>
/// <remarks>Thread-safe. All job state changes happen under one lock; events and file I/O happen outside it.</remarks>
public sealed class DownloadManager : IAsyncDisposable
{
    private const int BufferSize = 256 * 1024;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(4);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // The same bytes on every platform (the catalog is signed over them): no CRLF on Windows.
        NewLine = "\n",
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _http;
    private readonly string _stateFile;
    private readonly DownloadOptions _options;
    private readonly TimeProvider _time;
    private readonly TokenBucket _bucket;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly Channel<bool> _saveSignal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly Task _saver;
    private long _sequence;
    private bool _disposed;

    public DownloadManager(HttpClient http, string stateFile, DownloadOptions? options = null)
    {
        _http = http;
        _stateFile = stateFile;
        _options = options ?? new DownloadOptions();
        _time = _options.Time;
        _bucket = new TokenBucket(_time);
        _saver = Task.Run(SaveLoopAsync);
    }

    /// <summary>Raised for every state change and, while bytes flow, at most ten times a second per job.</summary>
    public event EventHandler<DownloadSnapshot>? Changed;

    /// <summary>Total bytes per second across all jobs; null is unlimited. Takes effect on the next chunk of every running job.</summary>
    public long? BandwidthLimit
    {
        get => _bucket.BytesPerSecond;
        set => _bucket.BytesPerSecond = value;
    }

    public int MaxConcurrent
    {
        get { lock (_gate) return _options.MaxConcurrent; }
        set
        {
            lock (_gate) _options.MaxConcurrent = Math.Max(1, value);
            Pump();
        }
    }

    public IReadOnlyList<DownloadSnapshot> Jobs
    {
        get { lock (_gate) return [.. _jobs.Values.OrderBy(j => j.Sequence).Select(Snapshot)]; }
    }

    public DownloadSnapshot? Get(string id)
    {
        lock (_gate) return _jobs.TryGetValue(id, out var job) ? Snapshot(job) : null;
    }

    /// <summary>The free bytes on the drive that holds <paramref name="folder"/>, or null when the system cannot say.</summary>
    public static long? DriveFreeSpace(string folder)
    {
        try
        {
            return new DriveInfo(Path.GetFullPath(folder)).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Restores the jobs of the last run from <c>downloads.json</c>: running ones start again (or come back paused when <see cref="DownloadOptions.AutoResume"/> is off).</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_stateFile)) return;
        List<PersistedJob>? saved;
        try
        {
            await using var stream = new FileStream(_stateFile, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            saved = await JsonSerializer.DeserializeAsync<List<PersistedJob>>(stream, Json, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Could not read {_stateFile}: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            foreach (var p in saved ?? [])
            {
                if (_jobs.ContainsKey(p.Id) || !Uri.TryCreate(p.Url, UriKind.Absolute, out var url)) continue;
                var request = new DownloadRequest(url, p.Destination, p.ExpectedSize, p.ExpectedSha256, p.Authenticated, p.Title);
                var state = p.State switch
                {
                    DownloadState.Paused or DownloadState.Failed => p.State,
                    _ => _options.AutoResume ? DownloadState.Queued : DownloadState.Paused,
                };
                var job = new Job(p.Id, request, ++_sequence) { State = state, Error = p.Error };
                job.Bytes = File.Exists(request.Destination + ".part") ? new FileInfo(request.Destination + ".part").Length : 0;
                if (state == DownloadState.Failed) job.Finished.TrySetResult();
                _jobs[p.Id] = job;
            }
        }

        Pump();
    }

    /// <summary>Queues a download and returns its job id. A request for a destination that already has an unfinished job returns that job's id.</summary>
    public string Enqueue(DownloadRequest request)
    {
        string id;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var existing = _jobs.Values.FirstOrDefault(j => !IsFinished(j.State)
                && string.Equals(Path.GetFullPath(j.Request.Destination), Path.GetFullPath(request.Destination), StringComparison.Ordinal));
            if (existing is not null) return existing.Id;
            id = Guid.NewGuid().ToString("N")[..12];
            _jobs[id] = new Job(id, request, ++_sequence);
        }

        Announce(id, force: true);
        Pump();
        return id;
    }

    /// <summary>Stops a job and keeps its partial file. A running job is paused as soon as its current read returns.</summary>
    public async Task PauseAsync(string id)
    {
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!) || IsFinished(job.State) || job.State == DownloadState.Paused) return;
            job.Intent = Intent.Pause;
            if (job.RunTask is null)
            {
                job.State = DownloadState.Paused;
                job.Intent = Intent.None;
            }
            else
            {
                job.Cts?.Cancel();
            }
        }

        if (job.RunTask is { } run) await run.ConfigureAwait(false);
        Announce(id, force: true);
        Save();
    }

    /// <summary>Queues a paused or failed job again; its partial file is picked up where it stopped.</summary>
    public void Resume(string id)
    {
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job) || job.State is not (DownloadState.Paused or DownloadState.Failed)) return;
            if (job.State == DownloadState.Failed) job.Finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            job.State = DownloadState.Queued;
            job.Error = null;
            job.Attempt = 0;
        }

        Announce(id, force: true);
        Pump();
    }

    /// <summary>Stops a job, deletes its partial file and sidecar, and forgets it.</summary>
    public async Task CancelAsync(string id)
    {
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!)) return;
            if (job.State == DownloadState.Done)
            {
                _jobs.Remove(id);
                return;
            }

            job.Intent = Intent.Cancel;
            job.Cts?.Cancel();
        }

        if (job.RunTask is { } run) await run.ConfigureAwait(false);
        lock (_gate)
        {
            if (job.State == DownloadState.Cancelled) return;
            job.State = DownloadState.Cancelled;
            _jobs.Remove(id);
        }

        DeletePart(job.Request.Destination);
        job.Finished.TrySetResult();
        Raise(Snapshot(job));
        Save();
    }

    /// <summary>Removes finished jobs (done, failed, cancelled) from the list.</summary>
    public void ClearFinished()
    {
        lock (_gate)
        {
            foreach (var id in _jobs.Where(p => IsFinished(p.Value.State)).Select(p => p.Key).ToList()) _jobs.Remove(id);
        }

        Save();
    }

    /// <summary>Completes when the job is done, failed or cancelled (a paused job keeps waiting).</summary>
    public async Task<DownloadSnapshot> WaitAsync(string id, CancellationToken ct = default)
    {
        Job job;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out job!)) throw new KeyNotFoundException($"No download {id}");
        }

        while (true)
        {
            Task finished;
            lock (_gate) finished = job.Finished.Task;
            await finished.WaitAsync(ct).ConfigureAwait(false);
            lock (_gate)
            {
                if (job.Finished.Task.IsCompleted) return Snapshot(job);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Task> runs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            runs = [];
            foreach (var job in _jobs.Values)
            {
                if (job.RunTask is null) continue;
                job.Intent = Intent.Shutdown;
                job.Cts?.Cancel();
                runs.Add(job.RunTask);
            }
        }

        await Task.WhenAll(runs).ConfigureAwait(false);
        _saveSignal.Writer.TryComplete();
        await _saver.ConfigureAwait(false);
        await SaveNowAsync().ConfigureAwait(false);
    }

    private static bool IsFinished(DownloadState state) => state is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled;

    private void Pump()
    {
        List<Job> starting = [];
        lock (_gate)
        {
            if (_disposed) return;
            var active = _jobs.Values.Count(j => j.RunTask is not null);
            foreach (var job in _jobs.Values.Where(j => j.State == DownloadState.Queued && j.RunTask is null).OrderBy(j => j.Sequence))
            {
                if (active >= _options.MaxConcurrent) break;
                active++;
                job.State = DownloadState.Running;
                job.Cts = new CancellationTokenSource();
                job.Intent = Intent.None;
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                job.RunTask = started.Task;
                job.RunStarted = started;
                starting.Add(job);
            }
        }

        foreach (var job in starting)
        {
            Announce(job.Id, force: true);
            _ = Task.Run(() => RunAsync(job));
        }

        if (starting.Count > 0) Save();
    }

    private async Task RunAsync(Job job)
    {
        var token = job.Cts!.Token;
        string? failure = null;
        var done = false;
        try
        {
            await TransferAsync(job, token).ConfigureAwait(false);
            done = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (PermanentDownloadException ex)
        {
            failure = ex.Message;
        }
        catch (TransientDownloadException ex)
        {
            failure = $"Gave up after {job.Attempt} attempts: {ex.Message}";
        }
        catch (Exception ex)
        {
            Log.Error($"Download {job.Request.Destination} failed unexpectedly", ex);
            failure = ex.Message;
        }

        bool removeFiles;
        lock (_gate)
        {
            job.Cts?.Dispose();
            job.Cts = null;
            removeFiles = false;
            if (done)
            {
                job.State = DownloadState.Done;
                job.Error = null;
            }
            else if (failure is not null)
            {
                job.State = DownloadState.Failed;
                job.Error = failure;
            }
            else
            {
                switch (job.Intent)
                {
                    case Intent.Pause:
                        job.State = DownloadState.Paused;
                        break;
                    case Intent.Cancel:
                        removeFiles = true;
                        break;
                    default:
                        // Shutdown: stay queued so the next start picks the job up again.
                        job.State = DownloadState.Queued;
                        break;
                }
            }

            job.Intent = Intent.None;
        }

        if (!removeFiles && IsFinished(job.State))
        {
            job.Finished.TrySetResult();
        }

        Announce(job.Id, force: true);
        Save();
        var started = job.RunStarted;
        lock (_gate)
        {
            job.RunTask = null;
        }

        started?.TrySetResult();
        Pump();
    }

    private async Task TransferAsync(Job job, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            lock (_gate) job.Attempt = attempt;
            var before = job.Bytes;
            try
            {
                await AttemptAsync(job, ct).ConfigureAwait(false);
                return;
            }
            catch (TransientDownloadException ex)
            {
                if (job.Bytes > before) attempt = 0;
                if (attempt >= _options.MaxAttempts) throw;
                var delay = ex.RetryAfter ?? Backoff(attempt);
                Log.Warn($"Download {job.Request.Destination}: {ex.Message}; retry {attempt}/{_options.MaxAttempts} in {delay.TotalSeconds:0.#} s");
                lock (_gate) job.Error = ex.Message;
                Announce(job.Id, force: true);
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(_options.BackoffCap.TotalSeconds, _options.BackoffBase.TotalSeconds * Math.Pow(2, attempt - 1));
        // Equal jitter: half fixed, half random, so a crowd of clients that failed together does not return together.
        return TimeSpan.FromSeconds(ceiling / 2 + Random.Shared.NextDouble() * ceiling / 2);
    }

    private async Task AttemptAsync(Job job, CancellationToken ct)
    {
        var request = job.Request;
        var part = request.Destination + ".part";
        var sidecarPath = part + ".json";
        var folder = Path.GetDirectoryName(Path.GetFullPath(request.Destination))!;
        Directory.CreateDirectory(folder);

        var sidecar = await ReadSidecarAsync(sidecarPath).ConfigureAwait(false);
        long offset = 0;
        if (File.Exists(part))
        {
            var length = new FileInfo(part).Length;
            var usable = sidecar is { ETag: { Length: > 0 } etag } && !etag.StartsWith("W/", StringComparison.Ordinal)
                         && sidecar.Url == request.Url.AbsoluteUri && length > 0
                         && (request.ExpectedSize is not { } expected || length < expected);
            if (usable) offset = length;
            else DeletePart(request.Destination);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
        var token = request.AuthToken ?? (request.Authenticated ? _options.TokenProvider?.Invoke() : null);
        if (token is { Length: > 0 }) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Byte offsets only mean something on the identity encoding.
        message.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (offset > 0)
        {
            message.Headers.Range = new RangeHeaderValue(offset, null);
            message.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(sidecar!.ETag!));
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new TransientDownloadException(ex.Message, ex);
        }

        using (response)
        {
            long? total;
            switch ((int)response.StatusCode)
            {
                case 200:
                    // The server ignored Range (or If-Range no longer matched): the whole file is coming.
                    offset = 0;
                    total = response.Content.Headers.ContentLength;
                    break;
                case 206:
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != offset)
                    {
                        DeletePart(request.Destination);
                        throw new TransientDownloadException($"Server answered the wrong range ({range})");
                    }

                    total = range.Length;
                    break;
                case 416:
                    DeletePart(request.Destination);
                    throw new TransientDownloadException("Server rejected the resume range; starting over");
                case 408 or 429 or >= 500:
                    var after = response.Headers.RetryAfter?.Delta;
                    throw new TransientDownloadException($"Server answered {(int)response.StatusCode}", retryAfter: after);
                default:
                    throw new PermanentDownloadException($"Server answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }

            if (request.ExpectedSize is { } want && total is { } have && want != have)
            {
                throw new PermanentDownloadException($"Expected {want} bytes but the server has {have}");
            }

            total ??= request.ExpectedSize;
            if (total is { } size)
            {
                var need = size - offset + _options.MinFreeBytes;
                if (_options.FreeSpace(folder) is { } free && free < need)
                {
                    throw new PermanentDownloadException(string.Create(CultureInfo.InvariantCulture, $"Not enough free space in {folder}: need {need / 1048576} MB, have {free / 1048576} MB"));
                }
            }

            var etagHeader = response.Headers.ETag?.ToString();
            await WriteSidecarAsync(sidecarPath, new Sidecar(request.Url.AbsoluteUri, etagHeader, total, request.ExpectedSha256)).ConfigureAwait(false);

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            FileStream file;
            try
            {
                file = new FileStream(part, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PermanentDownloadException($"Cannot write {part}: {ex.Message}", ex);
            }

            await using (file.ConfigureAwait(false))
            {
                var buffer = new byte[BufferSize];
                try
                {
                    // Resume: bring the hash up to the bytes already on disk before appending.
                    long hashed = 0;
                    while (hashed < offset)
                    {
                        var n = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - hashed)), ct).ConfigureAwait(false);
                        if (n == 0) throw new PermanentDownloadException($"{part} is shorter than its recorded length");
                        hash.AppendData(buffer, 0, n);
                        hashed += n;
                    }
                }
                catch (IOException ex) when (ex is not PermanentDownloadException)
                {
                    throw new PermanentDownloadException($"Cannot read {part}: {ex.Message}", ex);
                }

                file.Position = offset;
                lock (_gate)
                {
                    job.Bytes = offset;
                    job.Total = total;
                    job.Samples.Clear();
                }

                Stream body;
                try
                {
                    body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    throw new TransientDownloadException(ex.Message, ex);
                }

                await using (body.ConfigureAwait(false))
                {
                    while (true)
                    {
                        int read;
                        try
                        {
                            read = await body.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException)
                        {
                            throw new TransientDownloadException(ex.Message, ex);
                        }

                        if (read == 0) break;
                        await _bucket.TakeAsync(read, ct).ConfigureAwait(false);
                        try
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        }
                        catch (IOException ex)
                        {
                            throw new PermanentDownloadException($"Cannot write {part}: {ex.Message}", ex);
                        }

                        hash.AppendData(buffer, 0, read);
                        long now;
                        lock (_gate)
                        {
                            job.Bytes += read;
                            now = job.Bytes;
                        }

                        if (request.ExpectedSize is { } cap && now > cap) throw new PermanentDownloadException($"The server sent more than the expected {cap} bytes");
                        Progress(job);
                    }
                }

                if (total is { } expectedTotal && job.Bytes != expectedTotal)
                {
                    await file.FlushAsync(ct).ConfigureAwait(false);
                    throw new TransientDownloadException($"Connection closed after {job.Bytes} of {expectedTotal} bytes");
                }

                lock (_gate) job.State = DownloadState.Verifying;
                Announce(job.Id, force: true);
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (request.ExpectedSha256 is { } wanted && !string.Equals(wanted, actual, StringComparison.OrdinalIgnoreCase))
                {
                    await file.DisposeAsync().ConfigureAwait(false);
                    DeletePart(request.Destination);
                    throw new PermanentDownloadException($"Checksum mismatch: expected {wanted}, got {actual}");
                }

                await file.FlushAsync(ct).ConfigureAwait(false);
                lock (_gate) job.Sha256 = actual;
            }

            try
            {
                File.Move(part, request.Destination, overwrite: true);
                File.Delete(sidecarPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new PermanentDownloadException($"Cannot move the finished file into place: {ex.Message}", ex);
            }
        }
    }

    private static void DeletePart(string destination)
    {
        foreach (var path in new[] { destination + ".part", destination + ".part.json" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not delete {path}: {ex.Message}");
            }
        }
    }

    private static async Task<Sidecar?> ReadSidecarAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync<Sidecar>(stream, Json).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task WriteSidecarAsync(string path, Sidecar sidecar)
    {
        try
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(sidecar, Json)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PermanentDownloadException($"Cannot write {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Raises a progress event if 100 ms have passed since the last one for this job.</summary>
    private void Progress(Job job)
    {
        var now = _time.GetTimestamp();
        lock (_gate)
        {
            if (_time.GetElapsedTime(job.LastRaise, now) < ProgressInterval) return;
            job.LastRaise = now;
        }

        Announce(job.Id, force: false);
    }

    private void Announce(string id, bool force)
    {
        DownloadSnapshot snapshot;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(id, out var job)) return;
            if (force) job.LastRaise = _time.GetTimestamp();
            snapshot = Snapshot(job);
        }

        Raise(snapshot);
    }

    private void Raise(DownloadSnapshot snapshot)
    {
        try
        {
            Changed?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            Log.Warn($"A download listener threw: {ex.Message}");
        }
    }

    /// <summary>Builds a snapshot; the caller holds the lock.</summary>
    private DownloadSnapshot Snapshot(Job job)
    {
        var now = _time.GetTimestamp();
        job.Samples.Enqueue((now, job.Bytes));
        while (job.Samples.Count > 1 && _time.GetElapsedTime(job.Samples.Peek().Stamp, now) > RateWindow) job.Samples.Dequeue();
        double rate = 0;
        if (job.State == DownloadState.Running && job.Samples.Count > 1)
        {
            var first = job.Samples.Peek();
            var seconds = _time.GetElapsedTime(first.Stamp, now).TotalSeconds;
            if (seconds > 0.2) rate = (job.Bytes - first.Bytes) / seconds;
        }

        TimeSpan? eta = job.Total is { } t && rate > 1 ? TimeSpan.FromSeconds(Math.Max(0, t - job.Bytes) / rate) : null;
        return new DownloadSnapshot(job.Id, job.Request, job.State, job.Bytes, job.Total ?? job.Request.ExpectedSize, rate, eta, job.Error, job.Attempt, job.Sha256);
    }

    private void Save() => _saveSignal.Writer.TryWrite(true);

    private async Task SaveLoopAsync()
    {
        await foreach (var _ in _saveSignal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            await SaveNowAsync().ConfigureAwait(false);
        }
    }

    private async Task SaveNowAsync()
    {
        List<PersistedJob> items;
        lock (_gate)
        {
            items = [.. _jobs.Values.Where(j => j.State is not (DownloadState.Done or DownloadState.Cancelled)).OrderBy(j => j.Sequence).Select(j => new PersistedJob(
                j.Id, j.Request.Url.AbsoluteUri, j.Request.Destination, j.Request.ExpectedSize, j.Request.ExpectedSha256, j.Request.Authenticated, j.Request.Title, j.State, j.Error))];
        }

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_stateFile))!);
            var temp = _stateFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(items, Json)).ConfigureAwait(false);
            File.Move(temp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save {_stateFile}: {ex.Message}");
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private enum Intent
    {
        None,
        Pause,
        Cancel,
        Shutdown,
    }

    private sealed class Job(string id, DownloadRequest request, long sequence)
    {
        public string Id { get; } = id;

        public DownloadRequest Request { get; } = request;

        public long Sequence { get; } = sequence;

        public DownloadState State { get; set; } = DownloadState.Queued;

        public long Bytes { get; set; }

        public long? Total { get; set; }

        public string? Error { get; set; }

        public int Attempt { get; set; }

        public string? Sha256 { get; set; }

        public Intent Intent { get; set; }

        public CancellationTokenSource? Cts { get; set; }

        public Task? RunTask { get; set; }

        public TaskCompletionSource? RunStarted { get; set; }

        public TaskCompletionSource Finished { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long LastRaise { get; set; }

        public Queue<(long Stamp, long Bytes)> Samples { get; } = new();
    }

    private sealed record Sidecar(string Url, string? ETag, long? TotalSize, string? ExpectedSha256);

    private sealed record PersistedJob(
        string Id,
        string Url,
        string Destination,
        long? ExpectedSize,
        string? ExpectedSha256,
        bool Authenticated,
        string? Title,
        DownloadState State,
        string? Error);
}
