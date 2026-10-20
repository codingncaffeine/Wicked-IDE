namespace Wicked.Core;

/// <summary>What a notice is about, in the order the footer ranks them when several are waiting.</summary>
public enum NoticeKind
{
    /// <summary>A passing word ("Saved"); clears itself.</summary>
    Info,

    /// <summary>Something finished well; clears itself.</summary>
    Success,

    /// <summary>Something to act on when convenient: a model update, a newer pick for this machine.</summary>
    Action,

    Warning,

    Error,

    /// <summary>Work under way with its progress: a download, an engine install.</summary>
    Progress,
}

/// <summary>
/// One line for the footer. A notice with a <see cref="Route"/> takes the user to the place where it is dealt with when
/// clicked; <see cref="Key"/> identifies what it is about, so a newer notice about the same thing replaces the older.
/// </summary>
/// <param name="Key">What the notice is about, e.g. <c>model-update:unsloth/Qwen3.8-27B-GGUF</c>.</param>
/// <param name="Text">The line the footer shows.</param>
/// <param name="Kind">Its kind, which decides its rank and its colour.</param>
/// <param name="Route">Where a click goes (see <c>ShellRoutes</c>), or null when there is nowhere to go.</param>
/// <param name="Progress">0–1 for a <see cref="NoticeKind.Progress"/> notice; null when the amount is unknown.</param>
public sealed record Notice(string Key, string Text, NoticeKind Kind, string? Route = null, double? Progress = null)
{
    /// <summary>Info and Success clear themselves after this long; every other kind stays until it is dealt with.</summary>
    public static readonly TimeSpan Dwell = TimeSpan.FromSeconds(4);

    public bool ClearsItself => Kind is NoticeKind.Info or NoticeKind.Success;
}

/// <summary>
/// Every notice waiting for the footer. Any thread may post or clear; <see cref="Changed"/> is raised on the thread that
/// changed the board, so the window marshals it to its own thread. The footer shows <see cref="Top"/>: work under way
/// first, then errors, warnings, things to act on, and passing words last, the newest first within a kind.
/// </summary>
public sealed class NoticeBoard
{
    private readonly Lock _gate = new();
    private readonly List<(Notice Notice, long Order)> _waiting = [];
    private readonly TimeProvider _time;
    private long _order;

    public NoticeBoard(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public event Action? Changed;

    /// <summary>The notice the footer shows, or null when nothing is waiting.</summary>
    public Notice? Top
    {
        get
        {
            lock (_gate)
            {
                return _waiting.Count == 0 ? null
                    : _waiting.OrderByDescending(w => w.Notice.Kind).ThenByDescending(w => w.Order).First().Notice;
            }
        }
    }

    /// <summary>How many notices are waiting, the one shown included.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _waiting.Count;
        }
    }

    /// <summary>Every waiting notice in the footer's order, for the full list.</summary>
    public IReadOnlyList<Notice> All
    {
        get
        {
            lock (_gate) return [.. _waiting.OrderByDescending(w => w.Notice.Kind).ThenByDescending(w => w.Order).Select(w => w.Notice)];
        }
    }

    /// <summary>Adds a notice, replacing any waiting notice with the same key.</summary>
    public void Post(Notice notice)
    {
        long order;
        lock (_gate)
        {
            _waiting.RemoveAll(w => w.Notice.Key == notice.Key);
            order = ++_order;
            _waiting.Add((notice, order));
        }

        if (notice.ClearsItself)
        {
            // Cleared only if it is still the same posting: a newer notice under the key keeps its own time.
            _ = Task.Delay(Notice.Dwell, _time).ContinueWith(_ => ClearIf(notice.Key, order), TaskScheduler.Default);
        }

        Changed?.Invoke();
    }

    /// <summary>Removes the notice with this key: the thing it was about has been dealt with.</summary>
    public void Clear(string key)
    {
        bool removed;
        lock (_gate) removed = _waiting.RemoveAll(w => w.Notice.Key == key) > 0;
        if (removed) Changed?.Invoke();
    }

    private void ClearIf(string key, long order)
    {
        bool removed;
        lock (_gate) removed = _waiting.RemoveAll(w => w.Notice.Key == key && w.Order == order) > 0;
        if (removed) Changed?.Invoke();
    }
}
