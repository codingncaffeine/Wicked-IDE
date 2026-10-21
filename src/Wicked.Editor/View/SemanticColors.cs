using Wicked.Editor.Text;

namespace Wicked.Editor.View;

/// <summary>One line's semantic stretches: [start, end) columns with their colours, sorted, for the line's text as it was.</summary>
public readonly record struct SemanticLine(string Text, int[] Starts, int[] Ends, uint[] Colors);

/// <summary>
/// The colours a language server's semantic tokens give names, by line, for the text at <see cref="Version"/>. The view
/// uses a line's stretches while the line still reads the same; an edited line shows the grammar's colours until fresh
/// tokens arrive.
/// </summary>
public sealed record SemanticColors(long Version, Rope Text, IReadOnlyDictionary<int, SemanticLine> Lines);
