using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

public sealed partial class DebugController
{
    // ── Variables ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The parts of a value, asked of the adapter on a worker: a long list comes as ranges.</summary>
    private Task<List<DebugNode>> LoadChildrenAsync(DebugNode node) => Task.Run(async () =>
    {
        var session = node.Session ?? throw new DapException("The session has ended");
        var list = new List<DebugNode>();
        DebugNode Make(DapVariable v) => new(DebugNodeKind.Variable, v.Name)
        {
            Value = v.Value,
            Type = v.Type,
            Reference = v.VariablesReference,
            Indexed = v.IndexedVariables,
            Named = v.NamedVariables,
            EvaluateName = v.EvaluateName,
            Session = session,
            FrameId = node.FrameId,
        };

        if (node.Kind == DebugNodeKind.Page)
        {
            foreach (var v in await session.VariablesAsync(node.Reference, "indexed", node.PageStart, node.PageCount)) list.Add(Make(v));
        }
        else if (node.Indexed is > DebugNode.PageSize)
        {
            if (node.Named is > 0) foreach (var v in await session.VariablesAsync(node.Reference, "named")) list.Add(Make(v));
            for (var start = 0; start < node.Indexed; start += DebugNode.PageSize)
            {
                var count = Math.Min(DebugNode.PageSize, node.Indexed.Value - start);
                list.Add(new DebugNode(DebugNodeKind.Page, $"[{start}..{start + count - 1}]") { Reference = node.Reference, PageStart = start, PageCount = count, Session = session, FrameId = node.FrameId, Indexed = count });
            }
        }
        else
        {
            foreach (var v in await session.VariablesAsync(node.Reference)) list.Add(Make(v));
        }

        foreach (var child in list) child.FrameId = node.FrameId;
        return list;
    });

    /// <summary>Double-click on a value: the new value is typed, sent to the adapter, and the frame read again.</summary>
    public void BeginEdit(DebugNode node)
    {
        if (!node.CanSet) return;
        node.EditText = node.Value;
        node.IsEditing = true;
    }

    public async Task CommitEditAsync(DebugNode node)
    {
        if (!node.IsEditing) return;
        node.IsEditing = false;
        var text = node.EditText.Trim();
        if (text.Length == 0 || text == node.Value || node.Session is not { } session || node.Parent is not { } parent) return;
        try
        {
            var set = await Task.Run(() => session.SetVariableAsync(parent.Reference, node.Name, text));
            node.Value = set.Value;
            node.Type = set.Type ?? node.Type;
            node.Reference = set.VariablesReference;
            await RefreshFrameAsync();
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Console.Write("stderr", $"{node.Name} could not be set: {ex.Message}\n");
            _notices.Post(new Notice("debug:set", $"{node.Name} could not be set: {ex.Message}", NoticeKind.Warning));
        }
    }

    public void CancelEdit(DebugNode node) => node.IsEditing = false;

    // ── Watch ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The watched expressions evaluated in a frame (or with no value when nothing is stopped).</summary>
    private async Task<List<DebugNode>> EvaluateWatchesAsync(DebugSession? session, int? frameId)
    {
        var nodes = new List<DebugNode>();
        foreach (var expression in WatchExpressions.ToList())
        {
            var node = new DebugNode(DebugNodeKind.Watch, expression) { Session = session, FrameId = frameId, EvaluateName = expression };
            if (session is { State: DebugSessionState.Stopped })
            {
                try
                {
                    var result = await session.EvaluateAsync(expression, frameId, "watch");
                    node.Value = result.Result;
                    node.Type = result.Type;
                    node.Reference = result.VariablesReference;
                    node.Indexed = result.IndexedVariables;
                    node.Named = result.NamedVariables;
                }
                catch (Exception ex) when (ex is DapException or IOException)
                {
                    node.Value = ex.Message;
                    node.IsError = true;
                }
            }

            nodes.Add(node);
        }

        return nodes;
    }

    private async Task ReevaluateWatchesAsync((DebugSession Session, int ThreadId, Wicked.Core.Debug.DapStackFrame Frame)? current)
    {
        var nodes = await Task.Run(() => EvaluateWatchesAsync(current?.Session, current?.Frame.Id));
        await WatchTree.SetRootsAsync(nodes);
    }

    /// <summary>The Add Expression button: a prompt for the expression.</summary>
    public void AddWatch(string? initial = null)
    {
        _shell.ShowPrompt("Expression to watch", initial ?? "", text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            AddWatchExpression(text.Trim());
        });
    }

    public void AddWatchExpression(string expression)
    {
        if (WatchExpressions.Contains(expression)) return;
        WatchExpressions.Add(expression);
        _ = ReevaluateWatchesAsync(_current);
        ScheduleSave();
    }

    public void EditWatch(DebugNode node)
    {
        var old = node.Name;
        _shell.ShowPrompt("Expression to watch", old, text =>
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var at = WatchExpressions.IndexOf(old);
            if (at < 0) return;
            WatchExpressions[at] = text.Trim();
            _ = ReevaluateWatchesAsync(_current);
            ScheduleSave();
        });
    }

    public void RemoveWatch(DebugNode node)
    {
        if (!WatchExpressions.Remove(node.Name)) return;
        _ = ReevaluateWatchesAsync(_current);
        ScheduleSave();
    }

    public void RemoveAllWatches()
    {
        WatchExpressions.Clear();
        _ = ReevaluateWatchesAsync(_current);
        ScheduleSave();
    }

    // ── The Debug Console's box ──────────────────────────────────────────────────────────────────

    /// <summary>An expression from the Debug Console, evaluated in the frame being looked at; its answer or its error is written under it.</summary>
    private async Task EvaluateInConsoleAsync(string expression)
    {
        var (session, frameId) = _current is { } c ? (c.Session, (int?)c.Frame.Id) : (Manager.Active, null);
        if (session is null)
        {
            Console.Write("error", "There is no debug session: start debugging to evaluate expressions\n");
            return;
        }

        try
        {
            var result = await Task.Run(() => session.EvaluateAsync(expression, frameId, "repl"));
            Console.Write("result", (result.Result.Length > 0 ? result.Result : "undefined") + "\n");
            if (_current is not null && result.Result.Length > 0) _ = RefreshFrameAsync();
        }
        catch (Exception ex) when (ex is DapException or IOException)
        {
            Console.Write("error", ex.Message + "\n");
        }
    }

    // ── Breakpoints ──────────────────────────────────────────────────────────────────────────────

    /// <summary>"Edit condition" on a breakpoint: choose what to edit, then type it.</summary>
    public void EditBreakpoint(SourceBreakpoint bp, string? kind = null)
    {
        void Ask(string what)
        {
            var (prompt, current) = what switch
            {
                "hit" => ("Hit count (for example 5, >= 5 or % 2): the breakpoint stops when it is reached", bp.HitCondition),
                "log" => ("Message to log when this line is reached; {expression} is replaced by its value", bp.LogMessage),
                _ => ("Condition: the breakpoint stops only when this expression is true", bp.Condition),
            };
            _shell.ShowPrompt(prompt, current ?? "", text =>
            {
                var value = string.IsNullOrWhiteSpace(text) ? null : text;
                Breakpoints.Update(bp.Id, b => what switch
                {
                    "hit" => b with { HitCondition = value },
                    "log" => b with { LogMessage = value, Condition = value is null ? b.Condition : null, HitCondition = value is null ? b.HitCondition : null },
                    _ => b with { Condition = value, LogMessage = value is null ? b.LogMessage : null },
                });
            });
        }

        if (kind is not null)
        {
            Ask(kind);
            return;
        }

        var items = new List<QuickItem>
        {
            new("Condition", bp.Condition ?? "stop only when an expression is true", "", () => { Ask("condition"); return Task.CompletedTask; }),
            new("Hit Count", bp.HitCondition ?? "stop when it has been reached this many times", "", () => { Ask("hit"); return Task.CompletedTask; }),
            new("Log Message", bp.LogMessage ?? "write a message instead of stopping", "", () => { Ask("log"); return Task.CompletedTask; }),
        };
        _shell.ShowChoices("Edit the breakpoint", items, null);
    }

    public void AddFunctionBreakpoint()
    {
        _shell.ShowPrompt("Name of the function to break on", "", text =>
        {
            if (!string.IsNullOrWhiteSpace(text)) Breakpoints.AddFunction(text.Trim());
        });
    }

    public void RemoveBreakpoint(BreakpointRow row)
    {
        switch (row.Kind)
        {
            case BreakpointRowKind.Source: Breakpoints.Remove(row.Id); break;
            case BreakpointRowKind.Function: Breakpoints.RemoveFunction(row.Id); break;
        }
    }

    /// <summary>Opens the file of a source breakpoint at its line.</summary>
    public Task GoToBreakpointAsync(BreakpointRow row) =>
        row is { Kind: BreakpointRowKind.Source, Path: { } path } ? _shell.OpenAtAsync(path, Math.Max(0, row.Line - 1), 0) : Task.CompletedTask;

    public void EditBreakpointRow(BreakpointRow row)
    {
        switch (row.Kind)
        {
            case BreakpointRowKind.Source when Breakpoints.Find(row.Id) is { } bp:
                EditBreakpoint(bp);
                break;
            case BreakpointRowKind.Function:
                var function = Breakpoints.Functions.FirstOrDefault(f => f.Id == row.Id);
                if (function is null) break;
                _shell.ShowPrompt("Condition: the breakpoint stops only when this expression is true", function.Condition ?? "", text => Breakpoints.UpdateFunction(function.Id, f => f with { Condition = text }));
                break;
            case BreakpointRowKind.Exception when row.Filter is { } filter && Breakpoints.Filters.FirstOrDefault(f => f.Filter == filter) is { SupportsCondition: true } setting:
                _shell.ShowPrompt("Condition: stop for this kind of exception only when this expression is true", setting.Condition ?? "", text => Breakpoints.SetFilterCondition(filter, text));
                break;
        }
    }

    // ── Choosing a configuration ─────────────────────────────────────────────────────────────────

    /// <summary>The palette's "Select and Start Debugging".</summary>
    public async Task SelectAndStartAsync()
    {
        if (_shell.Workbench.Root is not null) await ReloadLaunchFileAsync();
        var items = Configurations.Select(c => new QuickItem(c.Name, c.IsCompound ? "compound" : c.Type, "", () =>
        {
            SelectedConfiguration = c;
            return StartAsync(c.Name);
        })).ToList();
        if (items.Count == 0)
        {
            await CreateLaunchJsonAsync();
            return;
        }

        _shell.ShowChoices("Select a launch configuration to start", items, null);
    }
}
