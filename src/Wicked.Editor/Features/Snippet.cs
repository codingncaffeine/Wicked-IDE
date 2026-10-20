using System.Collections.Immutable;
using System.Text;

namespace Wicked.Editor.Features;

/// <summary>A node of a parsed snippet body (VS Code's snippet grammar).</summary>
public abstract record SnippetNode;

/// <summary>Literal text, escapes already resolved.</summary>
public sealed record SnippetText(string Text) : SnippetNode;

/// <summary><c>$1</c> or <c>${1}</c>: a tab stop, optionally with a transform (<c>${1/re/fmt/flags}</c>) that makes it a mirror of the stop's text.</summary>
public sealed record SnippetTabStop(int Index, SnippetTransform? Transform = null) : SnippetNode;

/// <summary><c>${1:default}</c>: a tab stop with default content, which may hold more nodes (nested placeholders).</summary>
public sealed record SnippetPlaceholder(int Index, ImmutableArray<SnippetNode> Children) : SnippetNode;

/// <summary><c>${1|a,b,c|}</c>: a tab stop that offers a list of choices; the first is inserted.</summary>
public sealed record SnippetChoice(int Index, ImmutableArray<string> Options) : SnippetNode;

/// <summary><c>$TM_FILENAME</c>, <c>${CURRENT_YEAR}</c>, <c>${name:default}</c>, <c>${name/re/fmt/}</c>.</summary>
public sealed record SnippetVariable(string Name, ImmutableArray<SnippetNode> Default, SnippetTransform? Transform = null) : SnippetNode;

/// <summary>A parsed snippet body.</summary>
public sealed record Snippet(ImmutableArray<SnippetNode> Nodes)
{
    /// <summary>
    /// Parses VS Code snippet syntax. Never throws: anything that is not valid snippet syntax (a stray <c>$</c>, an
    /// unterminated <c>${</c>) is kept as literal text, as VS Code does.
    /// </summary>
    public static Snippet Parse(string body)
    {
        var parser = new SnippetParser(body);
        return new Snippet(parser.ParseNodes(untilBrace: false));
    }

    /// <summary>True when the body has any tab stop, placeholder or choice (a body of text and variables just inserts).</summary>
    public bool HasTabStops => Walk(Nodes).Any(n => n is SnippetTabStop or SnippetPlaceholder or SnippetChoice);

    internal static IEnumerable<SnippetNode> Walk(IEnumerable<SnippetNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            var children = node switch
            {
                SnippetPlaceholder p => p.Children,
                SnippetVariable v => v.Default,
                _ => default,
            };
            if (!children.IsDefaultOrEmpty)
            {
                foreach (var inner in Walk(children)) yield return inner;
            }
        }
    }
}

/// <summary>The recursive-descent parser behind <see cref="Snippet.Parse"/>.</summary>
internal sealed class SnippetParser(string source)
{
    private int _pos;

    public ImmutableArray<SnippetNode> ParseNodes(bool untilBrace)
    {
        var nodes = ImmutableArray.CreateBuilder<SnippetNode>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            nodes.Add(new SnippetText(text.ToString()));
            text.Clear();
        }

        while (_pos < source.Length)
        {
            var c = source[_pos];
            if (c == '}' && untilBrace) break;
            if (c == '\\' && _pos + 1 < source.Length && source[_pos + 1] is '$' or '}' or '\\')
            {
                text.Append(source[_pos + 1]);
                _pos += 2;
                continue;
            }

            if (c == '$')
            {
                var save = _pos;
                var node = ParseDollar();
                if (node is not null)
                {
                    Flush();
                    nodes.Add(node);
                    continue;
                }

                _pos = save + 1;
                text.Append('$');
                continue;
            }

            text.Append(c);
            _pos++;
        }

        Flush();
        return nodes.ToImmutable();
    }

    private SnippetNode? ParseDollar()
    {
        _pos++; // '$'
        if (_pos >= source.Length) return null;
        var c = source[_pos];
        if (char.IsAsciiDigit(c)) return new SnippetTabStop(ReadInt());
        if (IsNameStart(c)) return new SnippetVariable(ReadName(), default);
        if (c != '{') return null;

        _pos++;
        if (_pos >= source.Length) return null;
        if (char.IsAsciiDigit(source[_pos]))
        {
            var index = ReadInt();
            if (Peek('}'))
            {
                _pos++;
                return new SnippetTabStop(index);
            }

            if (Peek(':'))
            {
                _pos++;
                var children = ParseNodes(untilBrace: true);
                if (!Peek('}')) return null;
                _pos++;
                return new SnippetPlaceholder(index, children);
            }

            if (Peek('|')) return ParseChoice(index);
            if (Peek('/'))
            {
                var transform = ParseTransform();
                return transform is null ? null : new SnippetTabStop(index, transform);
            }

            return null;
        }

        if (!IsNameStart(source[_pos])) return null;
        var name = ReadName();
        if (Peek('}'))
        {
            _pos++;
            return new SnippetVariable(name, default);
        }

        if (Peek(':'))
        {
            _pos++;
            var children = ParseNodes(untilBrace: true);
            if (!Peek('}')) return null;
            _pos++;
            return new SnippetVariable(name, children);
        }

        if (Peek('/'))
        {
            var transform = ParseTransform();
            return transform is null ? null : new SnippetVariable(name, default, transform);
        }

        return null;
    }

    private SnippetNode? ParseChoice(int index)
    {
        _pos++; // '|'
        var options = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        while (_pos < source.Length)
        {
            var c = source[_pos];
            if (c == '\\' && _pos + 1 < source.Length && source[_pos + 1] is ',' or '|' or '\\' or '$' or '}')
            {
                current.Append(source[_pos + 1]);
                _pos += 2;
            }
            else if (c == ',')
            {
                options.Add(current.ToString());
                current.Clear();
                _pos++;
            }
            else if (c == '|')
            {
                options.Add(current.ToString());
                _pos++;
                if (!Peek('}')) return null;
                _pos++;
                return new SnippetChoice(index, options.ToImmutable());
            }
            else
            {
                current.Append(c);
                _pos++;
            }
        }

        return null;
    }

    /// <summary>At '/': <c>/regex/format/options}</c>, consuming the closing brace.</summary>
    private SnippetTransform? ParseTransform()
    {
        _pos++; // '/'
        var regex = new StringBuilder();
        while (_pos < source.Length && source[_pos] != '/')
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length)
            {
                // "\/" is a literal slash; every other escape belongs to the regex.
                if (source[_pos + 1] == '/') regex.Append('/');
                else regex.Append(source[_pos]).Append(source[_pos + 1]);
                _pos += 2;
                continue;
            }

            regex.Append(source[_pos++]);
        }

        if (!Peek('/')) return null;
        _pos++;
        var format = ParseFormat();
        if (!Peek('/')) return null;
        _pos++;
        var flags = new StringBuilder();
        while (_pos < source.Length && char.IsAsciiLetter(source[_pos])) flags.Append(source[_pos++]);
        if (!Peek('}')) return null;
        _pos++;
        return SnippetTransform.Create(regex.ToString(), format, flags.ToString());
    }

    private ImmutableArray<SnippetFormat> ParseFormat()
    {
        var parts = ImmutableArray.CreateBuilder<SnippetFormat>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length == 0) return;
            parts.Add(new SnippetFormat(-1, null, null, null, text.ToString()));
            text.Clear();
        }

        while (_pos < source.Length && source[_pos] != '/')
        {
            var c = source[_pos];
            if (c == '\\' && _pos + 1 < source.Length)
            {
                text.Append(source[_pos + 1]);
                _pos += 2;
                continue;
            }

            if (c == '$' && _pos + 1 < source.Length)
            {
                var save = _pos;
                var part = ParseFormatGroup();
                if (part is not null)
                {
                    Flush();
                    parts.Add(part);
                    continue;
                }

                _pos = save;
            }

            text.Append(c);
            _pos++;
        }

        Flush();
        return parts.ToImmutable();
    }

    private SnippetFormat? ParseFormatGroup()
    {
        _pos++; // '$'
        if (char.IsAsciiDigit(source[_pos])) return new SnippetFormat(ReadInt(), null, null, null, null);
        if (source[_pos] != '{') return null;
        _pos++;
        if (_pos >= source.Length || !char.IsAsciiDigit(source[_pos])) return null;
        var group = ReadInt();
        if (Peek('}'))
        {
            _pos++;
            return new SnippetFormat(group, null, null, null, null);
        }

        if (!Peek(':')) return null;
        _pos++;
        string? modifier = null, ifSet = null, ifNot = null;
        if (Peek('/'))
        {
            _pos++;
            modifier = ReadWhile(char.IsAsciiLetter);
        }
        else if (Peek('+'))
        {
            _pos++;
            ifSet = ReadFormatText(':');
        }
        else if (Peek('?'))
        {
            _pos++;
            ifSet = ReadFormatText(':');
            if (!Peek(':')) return null;
            _pos++;
            ifNot = ReadFormatText('\0');
        }
        else
        {
            if (Peek('-')) _pos++;
            ifNot = ReadFormatText('\0');
        }

        if (!Peek('}')) return null;
        _pos++;
        return new SnippetFormat(group, modifier, ifSet, ifNot, null);
    }

    /// <summary>Text up to an unescaped '}' (or <paramref name="stop"/>), escapes resolved.</summary>
    private string ReadFormatText(char stop)
    {
        var sb = new StringBuilder();
        while (_pos < source.Length && source[_pos] != '}' && source[_pos] != stop)
        {
            if (source[_pos] == '\\' && _pos + 1 < source.Length)
            {
                sb.Append(source[_pos + 1]);
                _pos += 2;
            }
            else
            {
                sb.Append(source[_pos++]);
            }
        }

        return sb.ToString();
    }

    private bool Peek(char c) => _pos < source.Length && source[_pos] == c;

    private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private int ReadInt()
    {
        var start = _pos;
        while (_pos < source.Length && char.IsAsciiDigit(source[_pos])) _pos++;
        return int.TryParse(source.AsSpan(start, _pos - start), out var v) ? v : int.MaxValue;
    }

    private string ReadName()
    {
        var start = _pos;
        while (_pos < source.Length && (char.IsAsciiLetterOrDigit(source[_pos]) || source[_pos] == '_')) _pos++;
        return source[start.._pos];
    }

    private string ReadWhile(Func<char, bool> predicate)
    {
        var start = _pos;
        while (_pos < source.Length && predicate(source[_pos])) _pos++;
        return source[start.._pos];
    }
}
