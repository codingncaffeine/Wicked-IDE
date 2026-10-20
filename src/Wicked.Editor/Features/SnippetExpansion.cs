using System.Collections.Immutable;
using System.Text;

namespace Wicked.Editor.Features;

/// <summary>One place a tab stop occupies in the expanded text: [Start, End), relative to the insertion.</summary>
/// <param name="Start">Where the range starts.</param>
/// <param name="End">Where the range ends.</param>
/// <param name="IsPrimary">The occurrence the user edits; the others mirror its text.</param>
/// <param name="Transform">A mirror's transform, applied to the primary's text.</param>
public sealed record SnippetRange(int Start, int End, bool IsPrimary, SnippetTransform? Transform = null);

/// <summary>A tab stop of an expanded snippet: its number, where it sits (more than one place when mirrored), and its choices.</summary>
public sealed record SnippetStop(int Index, ImmutableArray<SnippetRange> Ranges, ImmutableArray<string> Choices)
{
    public SnippetRange Primary => Ranges.FirstOrDefault(r => r.IsPrimary) ?? Ranges[0];
}

/// <summary>
/// A snippet turned into text to insert plus its tab stops, in the order the user visits them: 1, 2, 3, ... and the final
/// stop (<c>$0</c>) last. All offsets are relative to the start of the inserted text.
/// </summary>
public sealed record ExpandedSnippet(string Text, ImmutableArray<SnippetStop> Stops)
{
    public bool HasStops => !Stops.IsEmpty;
}

/// <summary>Turns a <see cref="Snippet"/> into an <see cref="ExpandedSnippet"/>.</summary>
public static class SnippetExpander
{
    /// <summary>
    /// Expands <paramref name="snippet"/>. New lines in the snippet are indented with <paramref name="lineIndent"/> (the
    /// whitespace the line it is inserted into starts with), and leading tabs in the snippet become
    /// <paramref name="indentUnit"/> (the editor's indentation: spaces, or a tab). Variables resolve against
    /// <paramref name="context"/>; an unknown variable becomes a placeholder showing its name. When a snippet has tab
    /// stops but no <c>$0</c>, a final stop is added at its end.
    /// </summary>
    public static ExpandedSnippet Expand(Snippet snippet, SnippetContext? context = null, string lineIndent = "", string indentUnit = "\t")
    {
        var e = new Expander(context ?? new SnippetContext(), lineIndent, indentUnit);
        return e.Run(snippet);
    }

    private sealed class Expander(SnippetContext context, string lineIndent, string indentUnit)
    {
        private readonly StringBuilder _text = new();
        private readonly Dictionary<int, SnippetNode> _definitions = [];
        private readonly Dictionary<int, List<SnippetRange>> _ranges = [];
        private readonly Dictionary<int, ImmutableArray<string>> _choices = [];
        private readonly HashSet<int> _rendering = [];
        private readonly Dictionary<SnippetVariable, int> _unknown = new(ReferenceEqualityComparer.Instance);
        private bool _atLineStart = true;

        public ExpandedSnippet Run(Snippet snippet)
        {
            var maxIndex = 0;
            foreach (var node in Snippet.Walk(snippet.Nodes))
            {
                switch (node)
                {
                    case SnippetTabStop t when t.Index != int.MaxValue:
                        maxIndex = Math.Max(maxIndex, t.Index);
                        break;
                    case SnippetPlaceholder p:
                        maxIndex = Math.Max(maxIndex, p.Index);
                        _definitions.TryAdd(p.Index, p);
                        break;
                    case SnippetChoice c:
                        maxIndex = Math.Max(maxIndex, c.Index);
                        _definitions.TryAdd(c.Index, c);
                        break;
                }
            }

            // Unknown variables turn into placeholders numbered after the highest stop.
            foreach (var node in Snippet.Walk(snippet.Nodes))
            {
                if (node is SnippetVariable v && !SnippetContext.KnownVariables.Contains(v.Name)) _unknown[v] = ++maxIndex;
            }

            Render(snippet.Nodes);

            // A snippet with stops but no final one ends its session at the end of the text.
            if (_ranges.Count > 0 && !_ranges.ContainsKey(0))
            {
                _ranges[0] = [new SnippetRange(_text.Length, _text.Length, true)];
            }

            var stops = _ranges
                .OrderBy(kv => kv.Key == 0 ? int.MaxValue : kv.Key)
                .Select(kv => new SnippetStop(kv.Key, [.. kv.Value.OrderBy(r => r.Start).ThenBy(r => r.End)], _choices.GetValueOrDefault(kv.Key, [])))
                .ToImmutableArray();
            return new ExpandedSnippet(_text.ToString(), stops);
        }

        private void Render(ImmutableArray<SnippetNode> nodes)
        {
            foreach (var node in nodes) RenderNode(node);
        }

        private void RenderNode(SnippetNode node)
        {
            switch (node)
            {
                case SnippetText t:
                    Emit(t.Text);
                    break;

                case SnippetTabStop t:
                    RenderTabStop(t);
                    break;

                case SnippetPlaceholder p:
                {
                    var start = _text.Length;
                    var primary = ReferenceEquals(_definitions.GetValueOrDefault(p.Index), p);
                    if (_rendering.Add(p.Index))
                    {
                        Render(p.Children);
                        _rendering.Remove(p.Index);
                    }

                    AddRange(p.Index, new SnippetRange(start, _text.Length, primary));
                    break;
                }

                case SnippetChoice c:
                {
                    var start = _text.Length;
                    Emit(c.Options.Length > 0 ? c.Options[0] : "");
                    var primary = ReferenceEquals(_definitions.GetValueOrDefault(c.Index), c);
                    AddRange(c.Index, new SnippetRange(start, _text.Length, primary));
                    _choices.TryAdd(c.Index, c.Options);
                    break;
                }

                case SnippetVariable v:
                    RenderVariable(v);
                    break;
            }
        }

        private void RenderTabStop(SnippetTabStop t)
        {
            var start = _text.Length;
            var definition = _definitions.GetValueOrDefault(t.Index);
            if (t.Transform is not null)
            {
                Emit(t.Transform.Apply(definition is null ? "" : Flatten(definition)));
                AddRange(t.Index, new SnippetRange(start, _text.Length, false, t.Transform));
                return;
            }

            if (definition is SnippetPlaceholder p && _rendering.Add(t.Index))
            {
                Render(p.Children);
                _rendering.Remove(t.Index);
            }
            else if (definition is SnippetChoice c)
            {
                Emit(c.Options.Length > 0 ? c.Options[0] : "");
            }

            // A bare stop with no definition anywhere is the primary (an empty place to type).
            AddRange(t.Index, new SnippetRange(start, _text.Length, definition is null));
        }

        private void RenderVariable(SnippetVariable v)
        {
            if (_unknown.TryGetValue(v, out var index))
            {
                var start = _text.Length;
                Emit(v.Name);
                AddRange(index, new SnippetRange(start, _text.Length, true));
                return;
            }

            var value = context.Resolve(v.Name);
            if (v.Transform is not null) value = v.Transform.Apply(value ?? "");
            if (string.IsNullOrEmpty(value) && !v.Default.IsDefaultOrEmpty)
            {
                Render(v.Default);
                return;
            }

            Emit(value ?? "");
        }

        private void AddRange(int index, SnippetRange range)
        {
            if (!_ranges.TryGetValue(index, out var list)) _ranges[index] = list = [];
            list.Add(range);
        }

        /// <summary>The plain text of a definition's content, with no ranges recorded (for a transformed mirror).</summary>
        private string Flatten(SnippetNode definition)
        {
            var saved = _text.ToString();
            var savedLineStart = _atLineStart;
            var savedRanges = _ranges.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            var savedChoices = new Dictionary<int, ImmutableArray<string>>(_choices);
            _text.Clear();
            if (definition is SnippetPlaceholder p)
            {
                _rendering.Add(p.Index);
                Render(p.Children);
                _rendering.Remove(p.Index);
            }
            else if (definition is SnippetChoice c)
            {
                _text.Append(c.Options.Length > 0 ? c.Options[0] : "");
            }

            var flat = _text.ToString();
            _text.Clear().Append(saved);
            _atLineStart = savedLineStart;
            _ranges.Clear();
            foreach (var kv in savedRanges) _ranges[kv.Key] = kv.Value;
            _choices.Clear();
            foreach (var kv in savedChoices) _choices[kv.Key] = kv.Value;
            return flat;
        }

        /// <summary>Appends text, indenting after each line break and turning leading tabs into the editor's indent unit.</summary>
        private void Emit(string text)
        {
            foreach (var c in text)
            {
                if (c == '\n')
                {
                    _text.Append('\n').Append(lineIndent);
                    _atLineStart = true;
                }
                else if (c == '\t' && _atLineStart)
                {
                    _text.Append(indentUnit);
                }
                else
                {
                    if (c is not ' ') _atLineStart = false;
                    _text.Append(c);
                }
            }
        }
    }
}
