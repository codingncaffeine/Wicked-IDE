using Wicked.Core.Lsp;
using Wicked.Editor.Text;

namespace Wicked.App.Language;

/// <summary>
/// VS Code's word-based suggestions: every word of two or more letters in the given texts, offered where no language server
/// suggests anything. Built on a worker; the word being typed is not offered back to itself.
/// </summary>
public static class WordSuggestions
{
    /// <summary>The kind VS Code gives a word suggestion (Text).</summary>
    public const int TextKind = 1;

    private const int MaxWords = 5000;
    private const int MaxCharsPerText = 2_000_000;

    public static CompletionList From(IReadOnlyList<Rope> texts, Rope current, (int Start, int End) typed)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in texts)
        {
            var length = Math.Min(text.Length, MaxCharsPerText);
            var start = -1;
            for (var i = 0; i <= length && words.Count < MaxWords; i++)
            {
                var isWord = i < length && IsWordChar(text[i]);
                if (isWord && start < 0) start = i;
                else if (!isWord && start >= 0)
                {
                    // The word the caret is in (the typed part and whatever it runs into) is never offered back to itself.
                    var isTyped = ReferenceEquals(text, current) && start <= typed.Start && i >= typed.End;
                    if (i - start >= 2 && !char.IsDigit(text[start]) && !isTyped) words.Add(text.Substring(start, i - start));
                    start = -1;
                }
            }
        }

        return new CompletionList(false, [.. words.Select(w => new CompletionItem(w, TextKind))]);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}
