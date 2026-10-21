using System.Collections.Immutable;
using System.Text.Json;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Wicked.Editor.Model;

namespace Wicked.Editor.Syntax;

/// <summary>A grammar an extension added, with the files it claims and what its language configuration says.</summary>
/// <param name="LanguageId">The language id ("foolang").</param>
/// <param name="Scope">The grammar's scope name ("source.foo").</param>
/// <param name="GrammarPath">The grammar file (.tmLanguage.json or .json).</param>
/// <param name="Extensions">File extensions with the dot.</param>
/// <param name="FileNames">Whole file names.</param>
/// <param name="Options">Comment marks and bracket pairs over the defaults.</param>
/// <param name="FoldingStart">The configuration's region start pattern, or null.</param>
/// <param name="FoldingEnd">The configuration's region end pattern, or null.</param>
public sealed record AddedGrammar(string LanguageId, string Scope, string GrammarPath, IReadOnlyList<string> Extensions, IReadOnlyList<string> FileNames, EditOptions Options, string? FoldingStart, string? FoldingEnd)
{
    public LanguageInfo Info => new(LanguageId, Scope, Options);
}

/// <summary>
/// The grammars extensions add on top of the bundled ones. Read from any thread; adding and removing swap an immutable
/// list, and the syntax thread asks for a grammar by scope only when a document of that language is opened.
/// </summary>
public static class AddedGrammars
{
    private static ImmutableArray<AddedGrammar> _all = [];

    public static IReadOnlyList<AddedGrammar> All => ImmutableInterlocked.InterlockedCompareExchange(ref _all, default, default);

    public static IEnumerable<string> LanguageIds => All.Select(g => g.LanguageId);

    /// <summary>Adds a grammar; the handle takes it back. The files are read here, so call off the UI thread.</summary>
    public static IDisposable Add(string languageId, string scope, string grammarPath, IEnumerable<string>? extensions, IEnumerable<string>? fileNames, string? configurationPath)
    {
        if (!File.Exists(grammarPath)) throw new FileNotFoundException($"The grammar file {grammarPath} does not exist");
        // Read once now, so a file that cannot be parsed is refused at the extension that shipped it, not later on the syntax thread.
        using (var probe = new StreamReader(grammarPath)) _ = GrammarReader.ReadGrammarSync(probe);
        var (options, start, end) = configurationPath is null ? (EditOptions.Default with { LineComment = null, BlockComment = null }, null, null) : ReadConfiguration(configurationPath);
        var added = new AddedGrammar(languageId, scope, grammarPath, [.. (extensions ?? []).Select(e => e.StartsWith('.') ? e : "." + e)], [.. fileNames ?? []], options, start, end);
        ImmutableInterlocked.Update(ref _all, all => [.. all.Where(g => g.Scope != scope), added]);
        return new Handle(added);
    }

    private sealed class Handle(AddedGrammar added) : IDisposable
    {
        public void Dispose() => ImmutableInterlocked.Update(ref _all, all => all.Remove(added));
    }

    internal static AddedGrammar? For(string fileName, string extension)
    {
        var all = All;
        return all.FirstOrDefault(g => g.FileNames.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            ?? (extension.Length == 0 ? null : all.FirstOrDefault(g => g.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)));
    }

    internal static AddedGrammar? ForLanguage(string id) => All.FirstOrDefault(g => string.Equals(g.LanguageId, id, StringComparison.OrdinalIgnoreCase));

    internal static AddedGrammar? ForScope(string scope) => All.FirstOrDefault(g => g.Scope == scope);

    /// <summary>A VS Code language configuration: comments, bracket pairs (both ways of writing them) and region markers.</summary>
    private static (EditOptions Options, string? FoldStart, string? FoldEnd) ReadConfiguration(string path)
    {
        var options = EditOptions.Default with { LineComment = null, BlockComment = null };
        string? start = null;
        string? end = null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        if (root.TryGetProperty("comments", out var comments))
        {
            var line = comments.TryGetProperty("lineComment", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            (string, string)? block = comments.TryGetProperty("blockComment", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() == 2
                ? (b[0].GetString() ?? "", b[1].GetString() ?? "") : null;
            options = options with { LineComment = string.IsNullOrEmpty(line) ? null : line, BlockComment = block };
        }

        var pairs = new List<BracketPair>();
        if (root.TryGetProperty("autoClosingPairs", out var closing) && closing.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in closing.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2) pairs.Add(new BracketPair(pair[0].GetString() ?? "", pair[1].GetString() ?? ""));
                else if (pair.ValueKind == JsonValueKind.Object && pair.TryGetProperty("open", out var o) && pair.TryGetProperty("close", out var c)) pairs.Add(new BracketPair(o.GetString() ?? "", c.GetString() ?? ""));
            }
        }

        pairs.RemoveAll(p => p.Open.Length == 0 || p.Close.Length == 0);
        if (pairs.Count > 0) options = options with { Pairs = [.. pairs.Distinct()] };
        if (root.TryGetProperty("folding", out var folding) && folding.TryGetProperty("markers", out var markers))
        {
            start = markers.TryGetProperty("start", out var s) ? s.GetString() : null;
            end = markers.TryGetProperty("end", out var e) ? e.GetString() : null;
        }

        return (options, start, end);
    }
}

/// <summary>The bundled grammars, then those extensions added: what the syntax thread's TextMate registry reads through.</summary>
internal sealed class ExtensibleOptions : IRegistryOptions
{
    public IRawTheme GetTheme(string scopeName) => Languages.Registry.GetTheme(scopeName);

    public IRawTheme GetDefaultTheme() => Languages.Registry.GetDefaultTheme();

    public ICollection<string> GetInjections(string scopeName) => Languages.Registry.GetInjections(scopeName);

    public IRawGrammar GetGrammar(string scopeName)
    {
        if (AddedGrammars.ForScope(scopeName) is not { } added) return Languages.Registry.GetGrammar(scopeName);
        using var reader = new StreamReader(added.GrammarPath);
        return GrammarReader.ReadGrammarSync(reader);
    }
}
