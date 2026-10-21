using Wicked.Core.Lsp;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;
using Wicked.Editor.View;

namespace Wicked.App.Language;

/// <summary>The protocol's semantic tokens (five integers each, positions relative to the token before) as colours by line.</summary>
public static class SemanticDecoder
{
    public static SemanticColors Decode(int[] data, SemanticTokensLegend legend, SemanticPalette palette, Rope text, long version)
    {
        var lines = new Dictionary<int, (List<int> Starts, List<int> Ends, List<uint> Colors)>();
        var line = 0;
        var start = 0;
        var modifiers = new List<string>(4);
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            var deltaLine = data[i];
            line += deltaLine;
            start = deltaLine == 0 ? start + data[i + 1] : data[i + 1];
            var length = data[i + 2];
            var type = data[i + 3];
            var bits = data[i + 4];
            if (type < 0 || type >= legend.TokenTypes.Count || line >= text.LineCount || length <= 0) continue;
            modifiers.Clear();
            for (var m = 0; m < legend.TokenModifiers.Count && bits >> m != 0; m++)
            {
                if ((bits & (1 << m)) != 0) modifiers.Add(legend.TokenModifiers[m]);
            }

            var color = palette.ColorFor(legend.TokenTypes[type], modifiers);
            if (color == 0) continue;
            if (!lines.TryGetValue(line, out var entry)) lines[line] = entry = ([], [], []);
            entry.Starts.Add(start);
            entry.Ends.Add(start + length);
            entry.Colors.Add(color);
        }

        var result = new Dictionary<int, SemanticLine>(lines.Count);
        foreach (var (number, (starts, ends, colors)) in lines)
        {
            result[number] = new SemanticLine(text.Line(number), [.. starts], [.. ends], [.. colors]);
        }

        return new SemanticColors(version, text, result);
    }
}
