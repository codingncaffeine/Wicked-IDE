using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Lsp;

// The subset of the Language Server Protocol 3.17 the client speaks, as records. Names follow the specification; positions
// are UTF-16 code units (negotiated), which is what the editor's text uses, so no conversion happens anywhere.

public sealed record Position(int Line, int Character);

public sealed record Range(Position Start, Position End);

public sealed record Location(string Uri, Range Range);

public sealed record TextEdit(Range Range, string NewText);

public sealed record TextDocumentIdentifier(string Uri);

public sealed record VersionedTextDocumentIdentifier(string Uri, int Version);

public sealed record TextDocumentItem(string Uri, string LanguageId, int Version, string Text);

public sealed record TextDocumentPositionParams(TextDocumentIdentifier TextDocument, Position Position);

/// <summary>One change: a range and its new text, or (no range) the whole document.</summary>
public sealed record TextDocumentContentChangeEvent(string Text, Range? Range = null);

public sealed record DidOpenTextDocumentParams(TextDocumentItem TextDocument);

public sealed record DidChangeTextDocumentParams(VersionedTextDocumentIdentifier TextDocument, IReadOnlyList<TextDocumentContentChangeEvent> ContentChanges);

public sealed record DidSaveTextDocumentParams(TextDocumentIdentifier TextDocument, string? Text = null);

public sealed record DidCloseTextDocumentParams(TextDocumentIdentifier TextDocument);

public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}

public sealed record DiagnosticRelatedInformation(Location Location, string Message);

/// <summary>A problem the server reports. <c>Code</c> is a number or a string in the protocol, kept as JSON.</summary>
public sealed record Diagnostic(
    Range Range,
    string Message,
    DiagnosticSeverity? Severity = null,
    JsonElement? Code = null,
    string? Source = null,
    IReadOnlyList<int>? Tags = null,
    IReadOnlyList<DiagnosticRelatedInformation>? RelatedInformation = null,
    JsonElement? Data = null);

public sealed record PublishDiagnosticsParams(string Uri, IReadOnlyList<Diagnostic> Diagnostics, int? Version = null);

public sealed record MarkupContent(string Kind, string Value);

public enum InsertTextFormat
{
    PlainText = 1,
    Snippet = 2,
}

/// <summary>
/// A completion. <c>TextEdit</c> is kept as JSON: the protocol allows a <see cref="TextEdit"/> or an insert/replace pair.
/// <c>Documentation</c> is a string or <see cref="MarkupContent"/>, also kept as JSON (<see cref="LspShapes.Text"/> reads both).
/// </summary>
public sealed record CompletionItem(
    string Label,
    int? Kind = null,
    string? Detail = null,
    JsonElement? Documentation = null,
    string? SortText = null,
    string? FilterText = null,
    string? InsertText = null,
    InsertTextFormat? InsertTextFormat = null,
    JsonElement? TextEdit = null,
    IReadOnlyList<TextEdit>? AdditionalTextEdits = null,
    IReadOnlyList<string>? CommitCharacters = null,
    bool? Preselect = null,
    JsonElement? Command = null,
    JsonElement? Data = null);

public sealed record CompletionList(bool IsIncomplete, IReadOnlyList<CompletionItem> Items);

public sealed record CompletionContext(int TriggerKind, string? TriggerCharacter = null);

public sealed record CompletionParams(TextDocumentIdentifier TextDocument, Position Position, CompletionContext? Context = null);

public sealed record Hover(string Text, Range? Range);

public sealed record ParameterInformation(JsonElement Label, JsonElement? Documentation = null);

public sealed record SignatureInformation(string Label, JsonElement? Documentation = null, IReadOnlyList<ParameterInformation>? Parameters = null, int? ActiveParameter = null);

public sealed record SignatureHelp(IReadOnlyList<SignatureInformation> Signatures, int? ActiveSignature = null, int? ActiveParameter = null);

public sealed record ReferenceContext(bool IncludeDeclaration);

public sealed record ReferenceParams(TextDocumentIdentifier TextDocument, Position Position, ReferenceContext Context);

public sealed record RenameParams(TextDocumentIdentifier TextDocument, Position Position, string NewName);

/// <summary>Edits across files, flattened from <c>changes</c> or <c>documentChanges</c> (text edits only; file operations are listed apart).</summary>
public sealed record WorkspaceEdit(IReadOnlyDictionary<string, IReadOnlyList<TextEdit>> Changes, IReadOnlyList<JsonElement> FileOperations);

public sealed record FormattingOptions(int TabSize, bool InsertSpaces, bool? TrimTrailingWhitespace = null, bool? InsertFinalNewline = null, bool? TrimFinalNewlines = null);

public sealed record DocumentFormattingParams(TextDocumentIdentifier TextDocument, FormattingOptions Options);

public sealed record DocumentRangeFormattingParams(TextDocumentIdentifier TextDocument, Range Range, FormattingOptions Options);

/// <summary>A symbol of a document, nested (from <c>DocumentSymbol[]</c>) or flat (from <c>SymbolInformation[]</c>, no children).</summary>
public sealed record DocumentSymbol(string Name, int Kind, Range Range, Range SelectionRange, string? Detail, IReadOnlyList<DocumentSymbol> Children);

public sealed record WorkspaceSymbol(string Name, int Kind, Location Location, string? ContainerName);

public sealed record CodeActionContext(IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string>? Only = null, int? TriggerKind = null);

public sealed record CodeActionParams(TextDocumentIdentifier TextDocument, Range Range, CodeActionContext Context);

/// <summary>A code action or a bare command; <c>Raw</c> is what to send back to resolve or execute it.</summary>
public sealed record CodeAction(string Title, string? Kind, bool IsPreferred, WorkspaceEdit? Edit, JsonElement? Command, JsonElement Raw);

public sealed record InlayHint(Position Position, string Label, int? Kind, bool PaddingLeft, bool PaddingRight);

public sealed record InlayHintParams(TextDocumentIdentifier TextDocument, Range Range);

public sealed record WorkspaceFolder(string Uri, string Name);

public sealed record ServerInfo(string Name, string? Version = null);

/// <summary>What <c>initialize</c> answered: the capabilities kept as JSON (they are a deep union), plus the server's name.</summary>
public sealed record InitializeResult(JsonElement Capabilities, ServerInfo? ServerInfo = null);

/// <summary>Reads the protocol's union shapes into one shape each.</summary>
public static class LspShapes
{
    /// <summary>Text from a string, a <see cref="MarkupContent"/>, a MarkedString (<c>{language, value}</c>), or an array of them.</summary>
    public static string Text(JsonElement? element)
    {
        if (element is not { } e) return "";
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString()!,
            JsonValueKind.Object when e.TryGetProperty("language", out var language) && e.TryGetProperty("value", out var code) =>
                $"```{language.GetString()}\n{code.GetString()}\n```",
            JsonValueKind.Object when e.TryGetProperty("value", out var value) => value.GetString() ?? "",
            JsonValueKind.Array => string.Join("\n\n", e.EnumerateArray().Select(x => Text(x)).Where(t => t.Length > 0)),
            _ => "",
        };
    }

    /// <summary><c>Location | Location[] | LocationLink[] | null</c> as locations (a link's target selection range).</summary>
    public static IReadOnlyList<Location> Locations(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object) return [Location(result)];
        if (result.ValueKind != JsonValueKind.Array) return [];
        return [.. result.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(Location)];

        static Location Location(JsonElement e) => e.TryGetProperty("targetUri", out var target)
            ? new Location(target.GetString()!, Read<Range>(e.TryGetProperty("targetSelectionRange", out var sel) ? sel : e.GetProperty("targetRange")))
            : new Location(e.GetProperty("uri").GetString()!, Read<Range>(e.GetProperty("range")));
    }

    /// <summary><c>CompletionItem[] | CompletionList | null</c> as a list.</summary>
    public static CompletionList Completions(JsonElement result) => result.ValueKind switch
    {
        JsonValueKind.Array => new CompletionList(false, [.. result.EnumerateArray().Select(Read<CompletionItem>)]),
        JsonValueKind.Object => Read<CompletionList>(result),
        _ => new CompletionList(false, []),
    };

    public static Hover? Hover(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("contents", out var contents)) return null;
        var text = Text(contents);
        if (text.Length == 0) return null;
        return new Hover(text, result.TryGetProperty("range", out var range) ? Read<Range>(range) : null);
    }

    /// <summary><c>DocumentSymbol[] | SymbolInformation[] | null</c> as a tree (flat information has no children).</summary>
    public static IReadOnlyList<DocumentSymbol> Symbols(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return [];
        return [.. result.EnumerateArray().Select(Symbol)];

        static DocumentSymbol Symbol(JsonElement e)
        {
            var name = e.GetProperty("name").GetString()!;
            var kind = e.GetProperty("kind").GetInt32();
            var detail = e.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            if (e.TryGetProperty("location", out var location))
            {
                var range = Read<Range>(location.GetProperty("range"));
                return new DocumentSymbol(name, kind, range, range, detail, []);
            }

            var children = e.TryGetProperty("children", out var c) && c.ValueKind == JsonValueKind.Array ? c.EnumerateArray().Select(Symbol).ToList() : [];
            return new DocumentSymbol(name, kind, Read<Range>(e.GetProperty("range")), Read<Range>(e.GetProperty("selectionRange")), detail, children);
        }
    }

    /// <summary><c>changes</c> and the text edits of <c>documentChanges</c> by URI; create/rename/delete operations kept apart, in order.</summary>
    public static WorkspaceEdit? Edit(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object) return null;
        var changes = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        var operations = new List<JsonElement>();
        if (result.TryGetProperty("documentChanges", out var documentChanges) && documentChanges.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in documentChanges.EnumerateArray())
            {
                if (!change.TryGetProperty("textDocument", out var doc))
                {
                    operations.Add(change);
                    continue;
                }

                var list = Bucket(changes, doc.GetProperty("uri").GetString()!);
                // Annotated edits carry an annotationId beside range and newText: the same two members are all we read.
                list.AddRange(change.GetProperty("edits").EnumerateArray().Select(Read<TextEdit>));
            }
        }
        else if (result.TryGetProperty("changes", out var plain) && plain.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in plain.EnumerateObject()) Bucket(changes, file.Name).AddRange(file.Value.EnumerateArray().Select(Read<TextEdit>));
        }

        return new WorkspaceEdit(changes.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TextEdit>)kv.Value, StringComparer.Ordinal), operations);

        static List<TextEdit> Bucket(Dictionary<string, List<TextEdit>> changes, string uri)
        {
            if (!changes.TryGetValue(uri, out var list)) changes[uri] = list = [];
            return list;
        }
    }

    /// <summary><c>(Command | CodeAction)[] | null</c>: a bare command becomes an action with only that command.</summary>
    public static IReadOnlyList<CodeAction> Actions(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return [];
        return [.. result.EnumerateArray().Select(e =>
        {
            var title = e.GetProperty("title").GetString()!;
            if (e.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
            {
                return new CodeAction(title, null, false, null, e, e);
            }

            return new CodeAction(
                title,
                e.TryGetProperty("kind", out var kind) ? kind.GetString() : null,
                e.TryGetProperty("isPreferred", out var preferred) && preferred.ValueKind == JsonValueKind.True,
                e.TryGetProperty("edit", out var edit) ? Edit(edit) : null,
                e.TryGetProperty("command", out var cmd) ? cmd : null,
                e);
        })];
    }

    /// <summary>Inlay hints; a label of parts is joined into one string.</summary>
    public static IReadOnlyList<InlayHint> InlayHints(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Array) return [];
        return [.. result.EnumerateArray().Select(e =>
        {
            var label = e.GetProperty("label");
            var text = label.ValueKind == JsonValueKind.String
                ? label.GetString()!
                : string.Concat(label.EnumerateArray().Select(part => part.GetProperty("value").GetString()));
            return new InlayHint(
                Read<Position>(e.GetProperty("position")),
                text,
                e.TryGetProperty("kind", out var kind) ? kind.GetInt32() : null,
                e.TryGetProperty("paddingLeft", out var l) && l.ValueKind == JsonValueKind.True,
                e.TryGetProperty("paddingRight", out var r) && r.ValueKind == JsonValueKind.True);
        })];
    }

    public static T Read<T>(JsonElement e) => (T)(e.Deserialize(LspJson.Default.GetTypeInfo(typeof(T))!) ?? throw new JsonException($"null {typeof(T).Name}"));

    public static JsonElement Write<T>(T value) => JsonSerializer.SerializeToElement(value, LspJson.Default.GetTypeInfo(typeof(T))!);
}

/// <summary>The protocol's JSON: camelCase, nulls left out, enums as their numbers (as the specification sends them).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(Position))]
[JsonSerializable(typeof(Range))]
[JsonSerializable(typeof(Location))]
[JsonSerializable(typeof(TextEdit))]
[JsonSerializable(typeof(TextDocumentPositionParams))]
[JsonSerializable(typeof(DidOpenTextDocumentParams))]
[JsonSerializable(typeof(DidChangeTextDocumentParams))]
[JsonSerializable(typeof(DidSaveTextDocumentParams))]
[JsonSerializable(typeof(DidCloseTextDocumentParams))]
[JsonSerializable(typeof(PublishDiagnosticsParams))]
[JsonSerializable(typeof(CompletionItem))]
[JsonSerializable(typeof(CompletionList))]
[JsonSerializable(typeof(CompletionParams))]
[JsonSerializable(typeof(SignatureHelp))]
[JsonSerializable(typeof(ReferenceParams))]
[JsonSerializable(typeof(RenameParams))]
[JsonSerializable(typeof(DocumentFormattingParams))]
[JsonSerializable(typeof(DocumentRangeFormattingParams))]
[JsonSerializable(typeof(CodeActionParams))]
[JsonSerializable(typeof(InlayHintParams))]
[JsonSerializable(typeof(InitializeResult))]
[JsonSerializable(typeof(TextDocumentIdentifier))]
[JsonSerializable(typeof(WorkspaceFolder))]
[JsonSerializable(typeof(List<TextEdit>))]
[JsonSerializable(typeof(List<WorkspaceFolder>))]
[JsonSerializable(typeof(string))]
public sealed partial class LspJson : JsonSerializerContext;
