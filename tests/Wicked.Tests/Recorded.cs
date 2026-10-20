namespace Wicked.Tests;

/// <summary>
/// Progress reports kept in the order they were made, on the reporting thread. <see cref="Progress{T}"/> would hand them
/// to the thread pool, so the last report could land after the operation returned (seen on two CPUs).
/// </summary>
internal sealed class Recorded<T> : IProgress<T>
{
    private readonly List<T> _items = [];

    public IReadOnlyList<T> Items
    {
        get
        {
            lock (_items) return [.. _items];
        }
    }

    public void Report(T value)
    {
        lock (_items) _items.Add(value);
    }
}
