using System.Collections.Concurrent;
using System.Collections.Immutable;
using Wicked.Core.Tasks;

namespace Wicked.Tests.Tasks;

/// <summary>
/// A throwaway folder under the checkout's <c>scratch/</c> (never the system temp folder, never the real profile): a
/// workspace the tests plant files in, removed afterwards.
/// </summary>
internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) dir = dir.Parent;
        var scratch = Path.Combine(dir?.FullName ?? Path.GetTempPath(), "scratch", "tests");
        Root = Path.Combine(scratch, "t-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path_(string relative) => Path.Combine(Root, relative);

    public string Write(string relative, string text)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string WriteTasks(string json) => Write(Path.Combine(".vscode", "tasks.json"), json);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Collects what tasks write, by label.</summary>
internal sealed class RecordingOutput : ITaskOutput
{
    private readonly ConcurrentDictionary<string, List<string>> _lines = new();
    private readonly ConcurrentDictionary<string, TaskOutcome> _outcomes = new();

    public ITaskSink Open(string label, TaskPresentation presentation)
    {
        var list = _lines.GetOrAdd(label, _ => []);
        return new Sink(list, outcome => _outcomes[label] = outcome);
    }

    public string[] Lines(string label)
    {
        if (!_lines.TryGetValue(label, out var list)) return [];
        lock (list) return [.. list];
    }

    public TaskOutcome? Outcome(string label) => _outcomes.GetValueOrDefault(label);

    private sealed class Sink(List<string> list, Action<TaskOutcome> closed) : ITaskSink
    {
        public void Write(string line)
        {
            lock (list) list.Add(line);
        }

        public void Close(TaskOutcome outcome) => closed(outcome);
    }
}

/// <summary>A window stand-in: a fixed context and scripted answers to inputs.</summary>
internal sealed class FakeEnvironment(string root, string? file = null, int line = 0) : ITaskEnvironment
{
    public Dictionary<string, string?> Answers { get; } = [];

    public Task<VariableContext> SnapshotAsync() => Task.FromResult(new VariableContext(root, file, line));

    public Task<string?> AskAsync(TaskInput input, CancellationToken ct) => Task.FromResult(Answers.GetValueOrDefault(input.Id));
}

internal static class TaskTestExtensions
{
    public static async Task<T> Within<T>(this Task<T> task, int seconds = 60) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public static async Task Within(this Task task, int seconds = 60) => await task.WaitAsync(TimeSpan.FromSeconds(seconds));

    public static ImmutableArray<TaskDefinition> Labels(this TaskCatalog catalog) => [.. catalog.All];
}
