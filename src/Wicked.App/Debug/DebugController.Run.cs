using System.Text.Json;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

public sealed partial class DebugController
{
    // ── Starting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>F5: starts the chosen configuration, or (with a session stopped) goes on.</summary>
    public async Task StartOrContinueAsync()
    {
        if (Manager.Active is { State: DebugSessionState.Stopped }) await ContinueAsync();
        else if (Manager.Active is null) await StartAsync();
        else if (Manager.Active.State == DebugSessionState.Starting) return;
        else await PauseAsync();
    }

    /// <summary>Starts a configuration or compound by name (the picker's choice when none is given).</summary>
    public async Task StartAsync(string? name = null, bool noDebug = false)
    {
        if (_shell.Workbench.Root is not { } root)
        {
            _notices.Post(new Notice("debug:start", "Open a folder to start debugging", NoticeKind.Info));
            return;
        }

        LaunchFile file;
        try
        {
            file = await Task.Run(() => LaunchFile.ReadAsync(root));
        }
        catch (LaunchException ex)
        {
            Fail(ex.Message, $"tab/{LaunchFile.PathIn(root)}");
            return;
        }

        ApplyLaunchFile(file, SelectedConfiguration?.Name);
        if (file.Names.FirstOrDefault() is null)
        {
            await CreateLaunchJsonAsync();
            return;
        }

        name ??= SelectedConfiguration?.Name ?? file.Names.First();
        var context = BuildContext(root, file);
        if (Manager.Sessions.Count == 0) Console.Clear();
        try
        {
            await Task.Run(() => Launcher.StartAsync(file, name, context, CancellationToken.None, noDebug));
        }
        catch (DebugAdapterMissingException ex)
        {
            Console.Write("stderr", ex.Message + "\n");
            _notices.Post(new Notice("debug:error", ex.Message, NoticeKind.Warning, ex.Spec is { } spec ? $"debug/install/{spec.Id}" : null));
        }
        catch (Exception ex) when (ex is LaunchException or DapException or IOException or JsonException or OperationCanceledException)
        {
            Fail(ex.Message, "panel/debug-console");
        }
    }

    private void Fail(string message, string? route)
    {
        Console.Write("stderr", message + "\n");
        _notices.Post(new Notice("debug:error", message, NoticeKind.Error, route));
        _shell.BottomPanel.Show(PanelIds.DebugConsole);
    }

    /// <summary>The editor and the selection as the launch variables see them. UI thread.</summary>
    private LaunchContext BuildContext(string root, LaunchFile file)
    {
        var tab = _shell.Workbench.ActiveTab;
        string? selected = null;
        int? line = null;
        if (tab is not null)
        {
            var doc = tab.Document;
            if (!doc.Primary.IsEmpty && doc.Primary.Length < 10_000) selected = doc.Text.Substring(doc.Primary.Start, doc.Primary.Length);
            line = doc.Text.LineOf(doc.Primary.Active) + 1;
        }

        return new LaunchContext
        {
            WorkspaceFolder = root,
            ActiveFile = tab?.Path,
            SelectedText = selected,
            LineNumber = line,
            Inputs = file.Inputs,
            Ask = AskAsync,
            Command = RunCommandVariableAsync,
        };
    }

    private Task<string?> AskAsync(LaunchInput input, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            if (input.Type == "pickString")
            {
                var items = input.Options.Select(o => new Wicked.App.ViewModels.QuickItem(o, "", "", () =>
                {
                    answer.TrySetResult(o);
                    return Task.CompletedTask;
                })).ToList();
                _shell.ShowChoices(input.Description ?? input.Id, items, () => answer.TrySetResult(null));
            }
            else _shell.ShowPrompt(input.Description ?? input.Id, input.Default ?? "", text => answer.TrySetResult(text));
        });
        return answer.Task.WaitAsync(ct);
    }

    /// <summary>The <c>${command:…}</c> variables the debugger knows: the process picker.</summary>
    private async Task<string?> RunCommandVariableAsync(string name, CancellationToken ct)
    {
        if (name is not ("pickProcess" or "extension.pickNativeProcess" or "pickMyProcess")) return null;
        var process = await PickProcessAsync();
        return process?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A list of the running programs to choose from; null when cancelled.</summary>
    public async Task<ProcessEntry?> PickProcessAsync()
    {
        var processes = await Task.Run(ProcessLister.List);
        var answer = new TaskCompletionSource<ProcessEntry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            var items = processes.Select(p => new Wicked.App.ViewModels.QuickItem($"{p.Name}  {p.Id}", p.CommandLine, "", () =>
            {
                answer.TrySetResult(p);
                return Task.CompletedTask;
            })).ToList();
            _shell.ShowChoices("Select the process to attach to", items, () => answer.TrySetResult(null));
        });
        return await answer.Task;
    }

    // ── launch.json ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads .vscode/launch.json again (a worker reads, the picker is rebuilt).</summary>
    public async Task ReloadLaunchFileAsync(string? preferred = null)
    {
        if (_root is not { } root)
        {
            ApplyLaunchFile(LaunchFile.Empty, null);
            return;
        }

        LaunchFile file;
        try
        {
            file = await Task.Run(() => LaunchFile.ReadAsync(root));
            _notices.Clear("debug:launch");
        }
        catch (LaunchException ex)
        {
            _notices.Post(new Notice("debug:launch", ex.Message, NoticeKind.Warning, $"tab/{LaunchFile.PathIn(root)}"));
            file = LaunchFile.Empty;
        }

        ApplyLaunchFile(file, preferred ?? SelectedConfiguration?.Name);
    }

    private void ApplyLaunchFile(LaunchFile file, string? preferred)
    {
        _launchFile = file;
        Configurations.Clear();
        foreach (var c in file.Configurations) Configurations.Add(new ConfigurationChoice(c.Name, c.Type, false));
        foreach (var c in file.Compounds) Configurations.Add(new ConfigurationChoice(c.Name, "", true));
        SelectedConfiguration = Configurations.FirstOrDefault(c => c.Name == preferred) ?? Configurations.FirstOrDefault();
        HasConfigurations = Configurations.Count > 0;
    }

    /// <summary>Opens launch.json in the editor, writing the starter first when the folder has none.</summary>
    public async Task OpenLaunchJsonAsync()
    {
        if (_shell.Workbench.Root is not { } root) return;
        if (!await Task.Run(() => File.Exists(LaunchFile.PathIn(root)))) await CreateLaunchJsonAsync();
        else await _shell.Workbench.OpenAsync(LaunchFile.PathIn(root));
    }

    /// <summary>"Create a launch.json": the starter for the active file's language (or the folder's), written and opened.</summary>
    public async Task CreateLaunchJsonAsync()
    {
        if (_shell.Workbench.Root is not { } root) return;
        var path = LaunchFile.PathIn(root);
        if (await Task.Run(() => File.Exists(path)))
        {
            await _shell.Workbench.OpenAsync(path);
            return;
        }

        var language = await LaunchStarter.DetectAsync(root, _shell.Workbench.ActiveTab?.Language?.Id);
        if (language is null)
        {
            var choice = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var items = LaunchStarter.Languages.Select(l => new Wicked.App.ViewModels.QuickItem(l, "", "", () =>
            {
                choice.TrySetResult(l);
                return Task.CompletedTask;
            })).ToList();
            _shell.ShowChoices("Select the language to debug", items, () => choice.TrySetResult(null));
            language = await choice.Task;
            if (language is null) return;
        }

        var starter = await LaunchStarter.CreateAsync(root, language);
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return Wicked.Core.Files.TextFile.WriteAsync(path, starter.Text, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None);
        });
        await _shell.Workbench.OpenAsync(path);
        await ReloadLaunchFileAsync();
        if (starter.Adapter is { } adapter && await Task.Run(() => Locator.Find(adapter)) is null) await OfferInstallAsync(adapter.Id);
    }

    /// <summary>"Attach to Process": choose a running program; the configuration of the active file's language attaches to it.</summary>
    public async Task AttachToProcessAsync()
    {
        if (_shell.Workbench.Root is not { } root) return;
        var process = await PickProcessAsync();
        if (process is null) return;
        var language = await LaunchStarter.DetectAsync(root, _shell.Workbench.ActiveTab?.Language?.Id) ?? "csharp";
        var adapter = DebugAdapterCatalog.ForLanguage(language).FirstOrDefault();
        if (adapter is null)
        {
            Fail($"There is no debug adapter for {language} to attach with", null);
            return;
        }

        var type = adapter.Types[0];
        var properties = new System.Text.Json.Nodes.JsonObject { ["name"] = $"Attach to {process.Name}", ["type"] = type, ["request"] = "attach" };
        properties[type is "lldb" or "codelldb" ? "pid" : "processId"] = type is "lldb" or "codelldb" ? process.Id : process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var request = new DebugStartRequest($"Attach to {process.Name}", type, "attach", System.Text.Json.JsonSerializer.SerializeToElement(properties));
        if (Manager.Sessions.Count == 0) Console.Clear();
        try
        {
            await Task.Run(() => Manager.StartAsync(request));
        }
        catch (DebugAdapterMissingException ex)
        {
            _notices.Post(new Notice("debug:error", ex.Message, NoticeKind.Warning, ex.Spec is { } spec ? $"debug/install/{spec.Id}" : null));
        }
        catch (Exception ex) when (ex is DapException or IOException or OperationCanceledException)
        {
            Fail(ex.Message, "panel/debug-console");
        }
    }

    // ── Adapters ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A missing adapter's notice clicked: what installing runs, to confirm or leave.</summary>
    public async Task OfferInstallAsync(string adapterId)
    {
        if (DebugAdapterCatalog.ById(adapterId) is not { } spec) return;
        // Looking for Node, Python or pip walks the PATH: that is a worker's work.
        if (await Task.Run(() => DebugAdapterInstaller.Unavailable(spec)) is { } needs)
        {
            _notices.Post(new Notice($"debug:install:{spec.Id}", needs, NoticeKind.Warning));
            return;
        }

        var how = spec.Install == AdapterInstall.Pip ? $"pip install {spec.Package}" : $"download the latest release from github.com/{spec.Repository}";
        var items = new List<Wicked.App.ViewModels.QuickItem>
        {
            new($"Install {spec.Name}", $"{how}, into {Path.Combine(AdaptersFolder, spec.Id)}", "", () => InstallAsync(spec)),
            new("Not now", "", "", () => Task.CompletedTask),
        };
        _shell.ShowChoices($"{spec.Name} debugs {string.Join(", ", spec.Languages)} programs", items, null);
    }

    /// <summary>Installs an adapter into Wicked's folder; its progress is in the Output panel.</summary>
    public async Task InstallAsync(DebugAdapterSpec spec)
    {
        var key = $"debug:install:{spec.Id}";
        _notices.Post(new Notice(key, $"Installing {spec.Name}…", NoticeKind.Progress, $"output/{ConsoleChannel}"));
        var installer = new DebugAdapterInstaller(Wicked.Core.Http.WickedHttp.Shared, AdaptersFolder);
        try
        {
            await Task.Run(() => installer.InstallAsync(spec, new Progress<string>(line => _shell.Output.Write(ConsoleChannel, line))));
            _notices.Post(new Notice(key, $"{spec.Name} installed", NoticeKind.Success));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        {
            _shell.Output.Write(ConsoleChannel, ex.Message);
            _notices.Post(new Notice(key, $"{spec.Name} was not installed: {ex.Message}", NoticeKind.Error, $"output/{ConsoleChannel}"));
        }
    }

    // ── Running ──────────────────────────────────────────────────────────────────────────────────

    private DebugSession? Target => Manager.Active;

    /// <summary>Runs a request on the session being looked at, on a worker; the thread it concerns is read here, on the UI thread.</summary>
    private void Send(Func<DebugSession, int?, Task> action)
    {
        if (Target is not { } session) return;
        var thread = ThreadFor(session);
        _ = Task.Run(async () =>
        {
            try
            {
                await action(session, thread);
            }
            catch (Exception ex) when (ex is DapException or IOException)
            {
                _post(() => Console.Write("stderr", ex.Message + "\n"));
            }
        });
    }

    public Task ContinueAsync()
    {
        Send((s, thread) => s.ContinueAsync(thread));
        return Task.CompletedTask;
    }

    public Task PauseAsync()
    {
        Send((s, _) => s.PauseAsync());
        return Task.CompletedTask;
    }

    public Task StepOverAsync()
    {
        Send((s, thread) => s.StepOverAsync(thread));
        return Task.CompletedTask;
    }

    public Task StepIntoAsync()
    {
        Send((s, thread) => s.StepIntoAsync(thread));
        return Task.CompletedTask;
    }

    public Task StepOutAsync()
    {
        Send((s, thread) => s.StepOutAsync(thread));
        return Task.CompletedTask;
    }

    private int? ThreadFor(DebugSession s) => _current is { } c && c.Session == s ? c.ThreadId : null;

    public async Task RestartAsync()
    {
        if (Target is not { } session) return;
        try
        {
            await Task.Run(() => Manager.RestartAsync(session));
        }
        catch (Exception ex) when (ex is DapException or IOException or OperationCanceledException)
        {
            Fail(ex.Message, "panel/debug-console");
        }
    }

    /// <summary>Shift+F5: ends the session being looked at, or every one when it is the only kind of thing left.</summary>
    public Task StopAsync()
    {
        if (Target is not { } session) return Task.CompletedTask;
        _ = Task.Run(() => session.StopAsync());
        return Task.CompletedTask;
    }

    public Task StopAllAsync()
    {
        _ = Task.Run(() => Manager.StopAllAsync());
        return Task.CompletedTask;
    }

    // ── A stop ───────────────────────────────────────────────────────────────────────────────────

    private async Task HandleStopAsync(DebugSession session, DapStop stop)
    {
        var version = Interlocked.Increment(ref _stopVersion);
        try
        {
            var threads = await session.RefreshThreadsAsync();
            var stopped = new Dictionary<int, StackState>();
            foreach (var thread in threads)
            {
                if (!session.IsThreadStopped(thread.Id)) continue;
                var (frames, total) = await session.StackTraceAsync(thread.Id, 0, DebugSession.FramePage);
                stopped[thread.Id] = new StackState(frames, total);
            }

            var threadId = stop.ThreadId ?? session.StoppedThreadId ?? threads.FirstOrDefault()?.Id ?? 0;
            if (!stopped.TryGetValue(threadId, out var state) && stopped.Count > 0) (threadId, state) = (stopped.Keys.First(), stopped.Values.First());
            if (state is null || state.Frames.Count == 0 || version != _stopVersion || session.State != DebugSessionState.Stopped)
            {
                _post(() =>
                {
                    foreach (var (id, s) in stopped) _stacks[(session, id)] = s;
                    RebuildCallStack();
                });
                return;
            }

            // The top frame that has a file to show; failing that, the very top one.
            var frame = state.Frames.FirstOrDefault(f => f.Source?.Path is { } p && File.Exists(p)) ?? state.Frames[0];
            string? exception = null;
            if (stop.Reason == "exception" && await session.ExceptionInfoAsync(threadId) is { } info)
            {
                exception = $"{info.ExceptionId}: {info.Description}".TrimEnd(' ', ':');
            }

            var picked = (session, threadId, frame);
            var shown = await BuildFrameAsync(session, frame);
            _post(() =>
            {
                if (version != _stopVersion) return;
                foreach (var (id, s) in stopped) _stacks[(session, id)] = s;
                _current = picked;
                RebuildCallStack();
                UpdateState();
                if (exception is not null) Console.Write("stderr", $"Exception: {exception}\n");
                _ = ApplyFrameAsync(shown, openFile: true);
                if (_shell.Settings.DebugOpenDebug == "openOnDebugBreak") _shell.Navigate(ShellRoutes.Debug);
            });
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            _post(() => Console.Write("stderr", $"The stopped program could not be inspected: {ex.Message}\n"));
        }
    }

    /// <summary>What the views show for a frame: its scopes with the first one opened, the watches, the values for the editor.</summary>
    private sealed record FrameView(DebugSession Session, DapStackFrame Frame, List<DebugNode> Scopes, List<DebugNode> Watches);

    private async Task<FrameView> BuildFrameAsync(DebugSession session, DapStackFrame frame)
    {
        var scopes = await session.ScopesAsync(frame.Id);
        var roots = scopes.Select(scope => new DebugNode(DebugNodeKind.Scope, scope.Name)
        {
            Reference = scope.VariablesReference,
            Indexed = scope.IndexedVariables,
            Named = scope.NamedVariables,
            Session = session,
            FrameId = frame.Id,
            Expensive = scope.Expensive,
        }).ToList();
        return new FrameView(session, frame, roots, await EvaluateWatchesAsync(session, frame.Id));
    }

    private async Task ApplyFrameAsync(FrameView view, bool openFile)
    {
        await Variables.SetRootsAsync(view.Scopes, openFirstScope: true);
        await WatchTree.SetRootsAsync(view.Watches);
        MarkSelectedFrame();
        RefreshEditorMarks();
        ShowInlineValues(view.Session, view.Frame, view.Scopes.FirstOrDefault(s => !s.Expensive));
        if (openFile) await OpenFrameSourceAsync(view.Session, view.Frame);
    }

    /// <summary>Opens the frame's file at its line (the editor takes the keyboard there), fetching the text from the adapter when it only has a reference.</summary>
    private async Task OpenFrameSourceAsync(DebugSession session, DapStackFrame frame)
    {
        var path = frame.Source?.Path;
        if (path is null || !await Task.Run(() => File.Exists(path)))
        {
            if (frame.Source is not { SourceReference: > 0 } source) return;
            try
            {
                var text = await Task.Run(() => session.SourceAsync(source));
                var name = string.Concat((source.Name ?? "source").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                path = Path.Combine(_paths.Cache, "debug-sources", $"{session.Id}-{source.SourceReference}", name);
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    return File.WriteAllTextAsync(path, text);
                });
            }
            catch (Exception ex) when (ex is DapException or IOException or UnauthorizedAccessException)
            {
                return;
            }
        }

        await _shell.OpenAtAsync(path, Math.Max(0, frame.Line - 1), Math.Max(0, frame.Column - 1));
    }

    private void MarkSelectedFrame()
    {
        foreach (var row in CallStack) row.IsSelected = row.IsFrame && _current is { } c && row.Session == c.Session && row.ThreadId == c.ThreadId && row.Frame?.Id == c.Frame.Id;
    }

    /// <summary>A click on a frame in the Call Stack: its file and line, its variables, its watches.</summary>
    public async Task SelectFrameAsync(StackRow row)
    {
        if (row.Frame is not { } frame) return;
        _current = (row.Session, row.ThreadId, frame);
        Manager.Select(row.Session);
        MarkSelectedFrame();
        var version = Interlocked.Increment(ref _stopVersion);
        try
        {
            var view = await Task.Run(() => BuildFrameAsync(row.Session, frame));
            if (version != _stopVersion) return;
            await ApplyFrameAsync(view, openFile: true);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Console.Write("stderr", ex.Message + "\n");
        }
    }

    /// <summary>A click on a thread: it becomes the one steps and continues apply to.</summary>
    public async Task SelectThreadAsync(StackRow row)
    {
        Manager.Select(row.Session);
        if (!row.Session.IsThreadStopped(row.ThreadId)) return;
        if (!_stacks.TryGetValue((row.Session, row.ThreadId), out var state) || state.Frames.Count == 0) return;
        var frame = state.Frames.FirstOrDefault(f => f.Source?.Path is { } p && File.Exists(p)) ?? state.Frames[0];
        await SelectFrameAsync(new StackRow(StackRowKind.Frame, row.Session, frame.Name) { ThreadId = row.ThreadId, Frame = frame });
    }

    /// <summary>"Load more stack frames" clicked.</summary>
    public async Task LoadMoreFramesAsync(StackRow row)
    {
        if (!_stacks.TryGetValue((row.Session, row.ThreadId), out var state)) return;
        try
        {
            var (frames, total) = await Task.Run(() => row.Session.StackTraceAsync(row.ThreadId, state.Frames.Count, DebugSession.FramePage));
            if (frames.Count == 0) state.Total = state.Frames.Count;
            else
            {
                state.Frames.AddRange(frames);
                state.Total = Math.Max(total, state.Frames.Count);
            }

            RebuildCallStack();
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Console.Write("stderr", ex.Message + "\n");
        }
    }

    /// <summary>Everything the open frame shows read again (after a value was set, or the adapter said it is out of date).</summary>
    public async Task RefreshFrameAsync()
    {
        if (_current is not { } c || c.Session.State != DebugSessionState.Stopped) return;
        var version = Interlocked.Increment(ref _stopVersion);
        try
        {
            var view = await Task.Run(() => BuildFrameAsync(c.Session, c.Frame));
            if (version != _stopVersion) return;
            await ApplyFrameAsync(view, openFile: false);
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Console.Write("stderr", ex.Message + "\n");
        }
    }

    // ── The Call Stack ───────────────────────────────────────────────────────────────────────────

    /// <summary>The Call Stack rebuilt: sessions (when there are several), their threads, the frames of the stopped ones.</summary>
    public void RebuildCallStack()
    {
        CallStack.Clear();
        var sessions = Manager.Sessions;
        foreach (var session in sessions)
        {
            if (sessions.Count > 1) CallStack.Add(new StackRow(StackRowKind.Session, session, session.Name) { Detail = session.State == DebugSessionState.Stopped ? "PAUSED" : session.State.ToString().ToUpperInvariant() });
            var threads = session.Threads;
            if (threads.Count == 0 && session.State == DebugSessionState.Running) CallStack.Add(new StackRow(StackRowKind.Thread, session, "main") { Detail = "RUNNING", Depth = sessions.Count > 1 ? 1 : 0 });
            foreach (var thread in threads)
            {
                var stopped = session.IsThreadStopped(thread.Id);
                var depth = sessions.Count > 1 ? 1 : 0;
                CallStack.Add(new StackRow(StackRowKind.Thread, session, thread.Name) { ThreadId = thread.Id, Detail = stopped ? (session.Stop?.Title ?? "Paused").ToUpperInvariant() : "RUNNING", Depth = depth });
                if (!stopped || !_stacks.TryGetValue((session, thread.Id), out var state)) continue;
                foreach (var frame in state.Frames)
                {
                    var place = frame.Source?.Name ?? (frame.Source?.Path is { } p ? Path.GetFileName(p) : "");
                    CallStack.Add(new StackRow(StackRowKind.Frame, session, frame.Name)
                    {
                        ThreadId = thread.Id,
                        Frame = frame,
                        Detail = place.Length > 0 ? $"{place}:{frame.Line}" : "",
                        Subtle = frame.PresentationHint is "subtle" or "label",
                        Depth = depth + 1,
                    });
                }

                if (state.Total > state.Frames.Count) CallStack.Add(new StackRow(StackRowKind.More, session, "Load more stack frames") { ThreadId = thread.Id, Depth = depth + 1 });
            }
        }

        MarkSelectedFrame();
    }
}
