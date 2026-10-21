using System.Collections.Immutable;
using System.Globalization;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.Core.Testing;

namespace Wicked.App.Tasks;

/// <summary>One row of the Testing panel's tree.</summary>
public sealed class TestRowItem(TestRow row, bool isExpanded)
{
    public TestRow Row { get; } = row;

    public string Id => Row.Id;

    public string Name => Row.Name;

    public string Detail => Row.Detail;

    public bool HasDetail => Row.Detail.Length > 0 && Row.Kind != TestKind.Test;

    public Thickness Indent => new(Row.Depth * 14, 0, 0, 0);

    public bool HasChildren => Row.HasChildren;

    public bool IsExpanded { get; } = isExpanded;

    public bool IsProject => Row.Kind == TestKind.Project;

    public bool IsPassed => Row.State == TestState.Passed;

    public bool IsFailed => Row.State == TestState.Failed;

    public bool IsRunning => Row.State is TestState.Running or TestState.Queued;

    public bool IsSkipped => Row.State == TestState.Skipped;

    public bool IsUnknown => Row.State == TestState.Unknown;

    /// <summary>"12 ms", "1.4 s"; empty when the framework did not say.</summary>
    public string DurationText => Row.Duration is not { } d ? "" : d.TotalMilliseconds < 1000 ? $"{d.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms" : $"{d.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";

    /// <summary>A group's tally: "3/5" passed, red when any failed.</summary>
    public string Tally => Row.Kind == TestKind.Test || Row.Tests == 0 ? "" : Row.Failed > 0 ? $"{Row.Failed} failed" : Row.Passed > 0 ? $"{Row.Passed}/{Row.Tests}" : "";
}

/// <summary>A line of a failure's message or stack; a line that names a place in a file can be clicked.</summary>
public sealed record TestDetailLine(string Text, SourceLocation? Location, bool IsStack)
{
    public bool IsLink => Location is not null;

    public bool IsPlain => Location is null;
}

/// <summary>
/// The Testing panel: the test tree (project, group, test) with each result's status, duration and the failure's message
/// and stack. Everything that touches the disk or a process runs on workers in <see cref="TestService"/>; this class turns
/// its tree into rows on a worker too, and the UI thread only swaps the finished list in.
/// </summary>
public sealed partial class TestingViewModel : ObservableObject, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Action<Action> _post;
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private int _refreshPending;
    private bool _started;
    private string? _selectedId;

    public TestingViewModel(ShellViewModel shell, Action<Action> post, TestService service)
    {
        _shell = shell;
        _post = post;
        Service = service;
        service.Changed += ScheduleRefresh;
        service.BusyChanged += busy => _post(() => IsBusy = busy);
    }

    public TestService Service { get; }

    public BulkCollection<TestRowItem> Rows { get; } = [];

    public System.Collections.ObjectModel.ObservableCollection<TestDetailLine> Details { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyText = "Open a folder to find its tests.";

    [ObservableProperty]
    private bool _hasDetails;

    [ObservableProperty]
    private string _detailsTitle = "";

    public string? SelectedId => _selectedId;

    /// <summary>A badge for the rail: how many tests failed in the last run.</summary>
    [ObservableProperty]
    private int _failed;

    partial void OnFilterChanged(string value) => ScheduleRefresh();

    /// <summary>The panel was shown: the first time, the tests are looked for.</summary>
    public void EnsureStarted()
    {
        if (_started) return;
        _started = true;
        _ = RefreshAsync();
    }

    /// <summary>A different folder was opened: its tests, not the old folder's.</summary>
    public void FolderChanged()
    {
        Service.WorkspaceFolder = _shell.Workbench.Root;
        if (_started) _ = RefreshAsync();
        else ScheduleRefresh();
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task RefreshAsync()
    {
        _started = true;
        Service.WorkspaceFolder = _shell.Workbench.Root;
        if (Service.WorkspaceFolder is null)
        {
            ScheduleRefresh();
            return;
        }

        try
        {
            await Service.DiscoverAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    private Task RunAllAsync() => Guarded(() => Service.RunAllAsync());

    [RelayCommand]
    private Task RunFailedAsync() => Guarded(() => Service.RunFailedAsync());

    [RelayCommand]
    private Task RunOneAsync(TestRowItem row) => Guarded(() => Service.RunAsync([row.Id]));

    [RelayCommand]
    private void Stop() => Service.Cancel();

    private async Task Guarded(Func<Task> run)
    {
        EnsureStarted();
        try
        {
            await run();
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>A chevron clicked: the group's tests folded away or shown again.</summary>
    [RelayCommand]
    private void Toggle(TestRowItem row)
    {
        lock (_collapsed)
        {
            if (!_collapsed.Remove(row.Id)) _collapsed.Add(row.Id);
        }

        ScheduleRefresh();
    }

    public void Expand(TestRowItem row, bool open)
    {
        if (!row.HasChildren || open == row.IsExpanded) return;
        Toggle(row);
    }

    /// <summary>Go to Test: the file and line of the test's code, opened in an editor.</summary>
    [RelayCommand]
    private async Task OpenAsync(TestRowItem row)
    {
        var place = await Service.LocateAsync(row.Id);
        if (place is { } p) await _shell.OpenAtAsync(p.File, Math.Max(0, p.Line - 1), 0);
    }

    [RelayCommand]
    private Task OpenDetailAsync(TestDetailLine line) =>
        line.Location is { } l ? _shell.OpenAtAsync(l.File, Math.Max(0, l.Line - 1), Math.Max(0, l.Column - 1)) : Task.CompletedTask;

    /// <summary>A row was picked: its failure shown under the tree.</summary>
    public void Select(TestRowItem? row)
    {
        _selectedId = row?.Id;
        _ = ShowDetailsAsync(row);
    }

    private async Task ShowDetailsAsync(TestRowItem? row)
    {
        if (row is null || row.Row.State is TestState.Unknown or TestState.Queued or TestState.Running)
        {
            Details.Clear();
            HasDetails = false;
            return;
        }

        var projectFolder = Service.WorkspaceFolder ?? "";
        var lines = await Task.Run(() => LinesOf(row.Row, projectFolder));
        if (_selectedId != row.Id) return;
        Details.Clear();
        foreach (var line in lines) Details.Add(line);
        DetailsTitle = row.Name;
        HasDetails = Details.Count > 0;
    }

    /// <summary>The message, then the stack (and what the test printed), each line with its source location when it has one.</summary>
    private static ImmutableArray<TestDetailLine> LinesOf(TestRow row, string workspace)
    {
        var folders = new List<string> { workspace };
        if (row.File is { } f && Path.GetDirectoryName(f) is { } dir) folders.Add(dir);
        var lines = ImmutableArray.CreateBuilder<TestDetailLine>();
        void Add(string? text, bool stack)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
            {
                var line = raw.TrimEnd();
                lines.Add(new TestDetailLine(line, TestLocations.Find(line, folders), stack));
            }
        }

        Add(row.Message, false);
        Add(row.Stack, true);
        if (row.State != TestState.Failed) Add(row.Output, false);
        return lines.ToImmutable();
    }

    // ── Building the rows ────────────────────────────────────────────────────────────────────────

    /// <summary>The tree changed (from any thread): rows are rebuilt on a worker at most every 50 ms.</summary>
    private void ScheduleRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPending, 1) == 1) return;
        _ = Task.Delay(50).ContinueWith(_ => Build(), TaskScheduler.Default);
    }

    private void Build()
    {
        Volatile.Write(ref _refreshPending, 0);
        HashSet<string> set;
        lock (_collapsed)
        {
            set = new HashSet<string>(_collapsed, StringComparer.Ordinal);
        }

        var filter = Filter;
        var rows = Service.Flatten(set, filter);
        var items = rows.Select(r => new TestRowItem(r, r.HasChildren && !set.Contains(r.Id))).ToList();
        var counts = Service.Counts();
        var hasProjects = Service.HasProjects;
        var root = Service.WorkspaceFolder;
        _post(() =>
        {
            Rows.Reset(items);
            IsEmpty = items.Count == 0;
            EmptyText = root is null ? "Open a folder to find its tests."
                : IsBusy && !hasProjects ? "Looking for tests…"
                : filter.Trim().Length > 0 && hasProjects ? "No test matches the filter."
                : "No tests were found. Supported: dotnet (xunit, NUnit, MSTest), cargo, go, pytest, jest and vitest.";
            Failed = counts.Failed;
            Summary = counts.Total == 0 ? "" : counts.Passed + counts.Failed + counts.Skipped == 0 ? $"{counts.Total} tests" : $"{counts.Passed} passed, {counts.Failed} failed" + (counts.Skipped > 0 ? $", {counts.Skipped} skipped" : "") + $" of {counts.Total}";
        });
    }

    partial void OnIsBusyChanged(bool value) => ScheduleRefresh();

    // ── Commands for the palette ─────────────────────────────────────────────────────────────────

    public IEnumerable<WorkbenchCommand> Commands() =>
    [
        new("workbench.view.testing.focus", "Test", "Focus on Test Explorer View", null, () =>
        {
            _shell.Navigate(ShellRoutes.Testing);
            return Task.CompletedTask;
        }),
        new("testing.runAll", "Test", "Run All Tests", null, async () =>
        {
            _shell.Navigate(ShellRoutes.Testing);
            await RunAllAsync();
        }),
        new("testing.reRunFailTests", "Test", "Rerun Failed Tests", null, async () =>
        {
            _shell.Navigate(ShellRoutes.Testing);
            await RunFailedAsync();
        }),
        new("testing.refreshTests", "Test", "Refresh Tests", null, async () =>
        {
            _shell.Navigate(ShellRoutes.Testing);
            await RefreshAsync();
        }),
        new("testing.cancelRun", "Test", "Cancel Test Run", null, () =>
        {
            Stop();
            return Task.CompletedTask;
        }),
    ];

    public void Dispose() => Service.Dispose();
}
