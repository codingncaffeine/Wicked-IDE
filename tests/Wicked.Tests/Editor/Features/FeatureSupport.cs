using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

/// <summary>Shared helpers for the feature tests: real highlighting for small texts, synthetic snapshots for big ones, timing.</summary>
internal static class FeatureSupport
{
    /// <summary>Highlights <paramref name="text"/> with the real syntax service and waits until every line is done.</summary>
    public static SyntaxSnapshot Highlight(string text, string file, long version = 0)
    {
        using var service = new SyntaxService();
        var snapshots = new BlockingCollection<SyntaxSnapshot>();
        var rope = Rope.FromString(text);
        var session = service.Open(rope, version, Languages.ForFile(file), snapshots.Add);
        SyntaxSnapshot? latest = null;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (snapshots.TryTake(out var s, TimeSpan.FromMilliseconds(100))) latest = s;
            if (latest is not null && latest.ValidLines == rope.LineCount)
            {
                session.Close();
                return latest;
            }
        }

        throw new TimeoutException("highlighting did not settle");
    }

    /// <summary>A snapshot whose every line is one token of the given standard type (0 other, 1 comment, 2 string, 3 regex).</summary>
    public static SyntaxSnapshot Synthetic(int lines, Func<int, int[]> tokensOfLine, long version = 0)
    {
        var list = ImmutableList.CreateBuilder<LineTokens?>();
        for (var i = 0; i < lines; i++) list.Add(new LineTokens(tokensOfLine(i)));
        return new SyntaxSnapshot(version, list.ToImmutable(), lines, new ColorTable([0, 0xFF000000, 0xFFFF0000, 0xFF00FF00, 0xFF0000FF]));
    }

    /// <summary>Packed (start, attributes) pairs for tokens of the given standard types and foregrounds.</summary>
    public static int[] Tokens(params (int Start, int Type, int Foreground)[] tokens)
    {
        var packed = new int[tokens.Length * 2];
        for (var i = 0; i < tokens.Length; i++)
        {
            packed[i * 2] = tokens[i].Start;
            packed[i * 2 + 1] = (tokens[i].Type << 8) | (tokens[i].Foreground << 15);
        }

        return packed;
    }

    /// <summary>A C#-like file of <paramref name="lines"/> lines, indented, with brackets, blank lines and comments.</summary>
    public static string BigSource(int lines)
    {
        var sb = new StringBuilder(lines * 30);
        var n = 0;
        while (n < lines)
        {
            sb.Append("namespace N").Append(n).Append('\n');
            sb.Append("{\n");
            sb.Append("    public class C").Append(n).Append('\n');
            sb.Append("    {\n");
            sb.Append("        void M(int a, int b)\n");
            sb.Append("        {\n");
            sb.Append("            if (a > b) { return; } // note (not a bracket\n");
            sb.Append("\n");
            sb.Append("            var s = \"text ( [\";\n");
            sb.Append("            foreach (var x in new[] { a, b })\n");
            sb.Append("            {\n");
            sb.Append("                Use(x);\n");
            sb.Append("            }\n");
            sb.Append("        }\n");
            sb.Append("    }\n");
            sb.Append("}\n");
            n += 15;
        }

        return sb.ToString();
    }

    /// <summary>The best of a few runs, in milliseconds, so a cold JIT does not count.</summary>
    public static double TimeMs(Action run, int runs = 5)
    {
        run();
        var best = double.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            run();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
