using System.Text.Json;
using Wicked.Core.Lsp;

namespace Wicked.Tests.Lsp;

/// <summary>The client against a real server: clangd, where it is installed (most Linux desktops with a C toolchain have it).</summary>
public sealed class LanguageClientTests : IDisposable
{
    private readonly TempProfile _profile = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _profile.Dispose();

    private static string? Clangd => new[] { "/usr/bin/clangd", "/usr/local/bin/clangd" }.FirstOrDefault(File.Exists);

    private const string Source = """
        #include <stdio.h>

        static int twice(int value) { return value * 2; }

        int main(void) {
            int result = twice(21);
            printf("%d\n", result);
            return undefined_name;
        }
        """;

    private async Task<(LanguageClient Client, string Uri, string Root)> StartAsync()
    {
        var root = Path.Combine(_profile.Root, "project");
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "main.c");
        await File.WriteAllTextAsync(file, Source, Ct);
        var client = await LanguageClient.StartAsync(
            new LanguageServerCommand(Clangd!, ["--log=error", "--background-index=false"]), root, Path.Combine(_profile.Root, "clangd.log"), Ct);
        return (client, LspPaths.ToUri(file), root);
    }

    [Fact]
    public async Task ARealServerStartsReportsProblemsAndAnswers()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        var (client, uri, _) = await StartAsync();
        await using var _ = client;
        var diagnostics = new TaskCompletionSource<PublishDiagnosticsParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Diagnostics += (_, d) =>
        {
            if (d.Uri == uri) diagnostics.TrySetResult(d);
        };

        Assert.Equal("clangd", client.ServerName);
        Assert.Equal(2, client.SyncKind);
        Assert.True(client.Supports("hoverProvider"));
        Assert.Contains(".", client.CompletionTriggers);
        client.Open(uri, "c", 1, Source);

        var problems = await diagnostics.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var undefined = Assert.Single(problems.Diagnostics, d => d.Message.Contains("undefined_name", StringComparison.Ordinal));
        Assert.Equal(DiagnosticSeverity.Error, undefined.Severity);
        Assert.Equal(7, undefined.Range.Start.Line);

        // Hover and go to definition on the call of twice (line 5, column 18).
        var hover = await client.HoverAsync(uri, new Position(5, 18), Ct);
        Assert.Contains("twice", hover!.Text, StringComparison.Ordinal);
        var definition = Assert.Single(await client.GoToAsync("definition", uri, new Position(5, 18), Ct));
        Assert.Equal(uri, definition.Uri);
        Assert.Equal(2, definition.Range.Start.Line);

        var references = await client.ReferencesAsync(uri, new Position(2, 12), includeDeclaration: true, Ct);
        Assert.Equal(2, references.Count);

        var symbols = await client.DocumentSymbolsAsync(uri, Ct);
        Assert.Equal(["twice", "main"], symbols.Select(s => s.Name));

        var rename = await client.RenameAsync(uri, new Position(2, 12), "doubled", Ct);
        Assert.Equal(2, rename!.Changes[uri].Count);
        Assert.All(rename.Changes[uri], e => Assert.Equal("doubled", e.NewText));
    }

    [Fact]
    public async Task IncrementalChangesKeepTheServerInStep()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        var (client, uri, _) = await StartAsync();
        await using var _ = client;
        var latest = new TaskCompletionSource<PublishDiagnosticsParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        var version = 0;
        client.Diagnostics += (_, d) =>
        {
            if (d.Uri == uri && d.Version == version && version == 2) latest.TrySetResult(d);
        };
        version = 1;
        client.Open(uri, "c", 1, Source);

        // Fix the error with a range edit: undefined_name → result (line 7, columns 11..25).
        version = 2;
        client.Change(uri, 2, [new TextDocumentContentChangeEvent("result", new Wicked.Core.Lsp.Range(new Position(7, 11), new Position(7, 25)))], () => throw new InvalidOperationException("clangd takes increments"));

        var problems = await latest.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.DoesNotContain(problems.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

        // Completion after "res" inside main sees the local.
        var list = await client.CompletionAsync(uri, new Position(7, 14), null, Ct);
        Assert.Contains(list.Items, i => i.Label.Contains("result", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClosingShutsTheServerDown()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        var (client, _, _) = await StartAsync();
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Exited += (_, code) => exited.TrySetResult(code);

        await client.DisposeAsync();

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.False(client.IsRunning);
    }

    [Fact]
    public async Task AServerThatIsNotThereFailsToStart()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => LanguageClient.StartAsync(
            new LanguageServerCommand(Path.Combine(_profile.Root, "no-such-server"), []), _profile.Root, Path.Combine(_profile.Root, "x.log"), Ct));
    }
}

public sealed class LspShapesTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void LocationsComeFromEveryShape()
    {
        const string range = """{"start":{"line":1,"character":2},"end":{"line":1,"character":5}}""";
        Assert.Single(LspShapes.Locations(Json($$"""{"uri":"file:///a","range":{{range}}}""")));
        Assert.Equal(2, LspShapes.Locations(Json($$"""[{"uri":"file:///a","range":{{range}}},{"uri":"file:///b","range":{{range}}}]""")).Count);
        var link = Assert.Single(LspShapes.Locations(Json($$"""[{"targetUri":"file:///c","targetRange":{{range}},"targetSelectionRange":{{range}}}]""")));
        Assert.Equal("file:///c", link.Uri);
        Assert.Empty(LspShapes.Locations(Json("null")));
    }

    [Fact]
    public void HoverTextComesFromEveryShape()
    {
        Assert.Equal("plain", LspShapes.Hover(Json("""{"contents":"plain"}"""))!.Text);
        Assert.Equal("**md**", LspShapes.Hover(Json("""{"contents":{"kind":"markdown","value":"**md**"}}"""))!.Text);
        Assert.Equal("```c\nint x\n```\n\nnote", LspShapes.Hover(Json("""{"contents":[{"language":"c","value":"int x"},"note"]}"""))!.Text);
        Assert.Null(LspShapes.Hover(Json("""{"contents":""}""")));
    }

    [Fact]
    public void CompletionsAndEditsFlatten()
    {
        Assert.Equal(2, LspShapes.Completions(Json("""[{"label":"a"},{"label":"b"}]""")).Items.Count);
        Assert.True(LspShapes.Completions(Json("""{"isIncomplete":true,"items":[{"label":"a"}]}""")).IsIncomplete);

        const string edit = """{"range":{"start":{"line":0,"character":0},"end":{"line":0,"character":1}},"newText":"x"}""";
        var fromChanges = LspShapes.Edit(Json($$$"""{"changes":{"file:///a":[{{{edit}}},{{{edit}}}]}}"""))!;
        Assert.Equal(2, fromChanges.Changes["file:///a"].Count);
        var fromDocuments = LspShapes.Edit(Json($$"""{"documentChanges":[{"textDocument":{"uri":"file:///b","version":3},"edits":[{{edit}}]},{"kind":"create","uri":"file:///new"}]}"""))!;
        Assert.Single(fromDocuments.Changes["file:///b"]);
        Assert.Single(fromDocuments.FileOperations);
    }

    [Fact]
    public void SymbolsAndActionsReadBothForms()
    {
        const string range = """{"start":{"line":0,"character":0},"end":{"line":3,"character":1}}""";
        var tree = LspShapes.Symbols(Json($$"""[{"name":"C","kind":5,"range":{{range}},"selectionRange":{{range}},"children":[{"name":"m","kind":6,"range":{{range}},"selectionRange":{{range}}}]}]"""));
        Assert.Equal("m", Assert.Single(Assert.Single(tree).Children).Name);
        var flat = LspShapes.Symbols(Json($$$"""[{"name":"f","kind":12,"location":{"uri":"file:///a","range":{{{range}}}}}]"""));
        Assert.Empty(Assert.Single(flat).Children);

        var actions = LspShapes.Actions(Json("""[{"title":"Run","command":"x.run"},{"title":"Fix","kind":"quickfix","isPreferred":true,"edit":{"changes":{}}}]"""));
        Assert.Null(actions[0].Edit);
        Assert.True(actions[1].IsPreferred);
        Assert.NotNull(actions[1].Edit);
    }

    [Fact]
    public void UrisRoundTripPathsWithSpaces()
    {
        var path = Path.Combine(Path.GetTempPath(), "a folder", "b c.cs");
        var uri = LspPaths.ToUri(path);
        Assert.StartsWith("file://", uri, StringComparison.Ordinal);
        Assert.Contains("%20", uri, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(path), LspPaths.ToPath(uri));
        Assert.Null(LspPaths.ToPath("untitled:Untitled-1"));
    }
}
