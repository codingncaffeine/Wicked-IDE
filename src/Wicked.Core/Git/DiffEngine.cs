using System.Collections.Immutable;
using System.Diagnostics;

namespace Wicked.Core.Git;

/// <summary>
/// A stretch of lines that differ: <see cref="OrigCount"/> lines from <see cref="OrigStart"/> in the original were replaced by
/// <see cref="ModCount"/> lines from <see cref="ModStart"/> in the modified text. All 0-based. A pure insertion has no original
/// lines (its <see cref="OrigStart"/> is where it goes); a pure deletion has no modified ones.
/// </summary>
public readonly record struct LineBlock(int OrigStart, int OrigCount, int ModStart, int ModCount)
{
    public int OrigEnd => OrigStart + OrigCount;

    public int ModEnd => ModStart + ModCount;

    public bool IsInsertion => OrigCount == 0;

    public bool IsDeletion => ModCount == 0;
}

/// <summary>The blocks that turn one text into another. <see cref="TimedOut"/>: the time cap hit, and what was left is one coarse block.</summary>
public sealed record DiffResult(ImmutableArray<LineBlock> Blocks, bool TimedOut)
{
    public bool IsIdentical => Blocks.IsEmpty;
}

/// <summary>
/// A line diff of our own: Myers' O(ND) algorithm in linear space (the middle-snake form), on lines hashed to integers after
/// trimming what the two texts share at both ends. A time cap bounds the work on huge or very different files, as VS Code's
/// does: when it passes, the part not yet compared counts as one replaced block, so the answer is always a valid diff, only a
/// coarser one.
/// </summary>
public static class DiffEngine
{
    public static readonly TimeSpan DefaultCap = TimeSpan.FromMilliseconds(1500);

    /// <summary>Diffs two arrays of lines.</summary>
    public static DiffResult Diff(IReadOnlyList<string> original, IReadOnlyList<string> modified, TimeSpan? cap = null, CancellationToken ct = default)
    {
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        var a = Intern(original, ids);
        var b = Intern(modified, ids);
        return Diff(a, b, cap ?? DefaultCap, ct);
    }

    /// <summary>Diffs two arrays of already-numbered items (lines, or the tokens of a line).</summary>
    public static DiffResult Diff(int[] a, int[] b, TimeSpan cap, CancellationToken ct = default)
    {
        var work = new Work(a, b, Stopwatch.StartNew(), cap, ct);
        work.Run(0, a.Length, 0, b.Length);
        return new DiffResult(Blocks(work), work.TimedOut);
    }

    private static int[] Intern(IReadOnlyList<string> lines, Dictionary<string, int> ids)
    {
        var result = new int[lines.Count];
        for (var i = 0; i < result.Length; i++)
        {
            if (!ids.TryGetValue(lines[i], out var id))
            {
                id = ids.Count;
                ids[lines[i]] = id;
            }

            result[i] = id;
        }

        return result;
    }

    private static ImmutableArray<LineBlock> Blocks(Work w)
    {
        var blocks = ImmutableArray.CreateBuilder<LineBlock>();
        int i = 0, j = 0;
        while (i < w.A.Length || j < w.B.Length)
        {
            var aFlag = i < w.A.Length && w.Deleted[i];
            var bFlag = j < w.B.Length && w.Inserted[j];
            if (!aFlag && !bFlag)
            {
                i++;
                j++;
                continue;
            }

            int si = i, sj = j;
            while (i < w.A.Length && w.Deleted[i]) i++;
            while (j < w.B.Length && w.Inserted[j]) j++;
            blocks.Add(new LineBlock(si, i - si, sj, j - sj));
        }

        return blocks.ToImmutable();
    }

    private sealed class Work
    {
        private readonly Stopwatch _clock;
        private readonly TimeSpan _cap;
        private readonly CancellationToken _ct;
        private readonly int[] _down;
        private readonly int[] _up;
        private readonly int _max;
        private int _ticks;

        public Work(int[] a, int[] b, Stopwatch clock, TimeSpan cap, CancellationToken ct)
        {
            A = a;
            B = b;
            _clock = clock;
            _cap = cap;
            _ct = ct;
            Deleted = new bool[a.Length];
            Inserted = new bool[b.Length];
            _max = (2 * (a.Length + b.Length)) + 3; // diagonals reach 1.5 times the combined length either side of zero
            _down = new int[(2 * _max) + 2];
            _up = new int[(2 * _max) + 2];
        }

        public int[] A { get; }

        public int[] B { get; }

        public bool[] Deleted { get; }

        public bool[] Inserted { get; }

        public bool TimedOut { get; private set; }

        public void Run(int lo1, int hi1, int lo2, int hi2)
        {
            while (lo1 < hi1 && lo2 < hi2 && A[lo1] == B[lo2])
            {
                lo1++;
                lo2++;
            }

            while (lo1 < hi1 && lo2 < hi2 && A[hi1 - 1] == B[hi2 - 1])
            {
                hi1--;
                hi2--;
            }

            if (lo1 == hi1)
            {
                for (var j = lo2; j < hi2; j++) Inserted[j] = true;
                return;
            }

            if (lo2 == hi2)
            {
                for (var i = lo1; i < hi1; i++) Deleted[i] = true;
                return;
            }

            if (TimedOut || !MiddleSnake(lo1, hi1, lo2, hi2, out var x, out var y))
            {
                for (var i = lo1; i < hi1; i++) Deleted[i] = true;
                for (var j = lo2; j < hi2; j++) Inserted[j] = true;
                return;
            }

            Run(lo1, x, lo2, y);
            Run(x, hi1, y, hi2);
        }

        /// <summary>The point where the best forward and backward paths of the box meet. False when the time cap passed.</summary>
        private bool MiddleSnake(int lo1, int hi1, int lo2, int hi2, out int mx, out int my)
        {
            mx = my = 0;
            int downK = lo1 - lo2, upK = hi1 - hi2;
            var delta = (hi1 - lo1) - (hi2 - lo2);
            var odd = (delta & 1) != 0;
            _down[_max + downK + 1] = lo1;
            _up[_max + upK - 1] = hi1;
            var maxD = (((hi1 - lo1) + (hi2 - lo2)) / 2) + 1;
            for (var d = 0; d <= maxD; d++)
            {
                if ((++_ticks & 63) == 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (_clock.Elapsed > _cap)
                    {
                        TimedOut = true;
                        return false;
                    }
                }

                for (var k = downK - d; k <= downK + d; k += 2)
                {
                    int x;
                    if (k == downK - d)
                    {
                        x = _down[_max + k + 1];
                    }
                    else
                    {
                        x = _down[_max + k - 1] + 1;
                        if (k < downK + d && _down[_max + k + 1] >= x) x = _down[_max + k + 1];
                    }

                    var y = x - k;
                    while (x < hi1 && y < hi2 && A[x] == B[y])
                    {
                        x++;
                        y++;
                    }

                    _down[_max + k] = x;
                    if (odd && upK - d < k && k < upK + d && _up[_max + k] <= _down[_max + k])
                    {
                        mx = _down[_max + k];
                        my = mx - k;
                        return true;
                    }
                }

                for (var k = upK - d; k <= upK + d; k += 2)
                {
                    int x;
                    if (k == upK + d)
                    {
                        x = _up[_max + k - 1];
                    }
                    else
                    {
                        x = _up[_max + k + 1] - 1;
                        if (k > upK - d && _up[_max + k - 1] < x) x = _up[_max + k - 1];
                    }

                    var y = x - k;
                    while (x > lo1 && y > lo2 && A[x - 1] == B[y - 1])
                    {
                        x--;
                        y--;
                    }

                    _up[_max + k] = x;
                    if (!odd && downK - d <= k && k <= downK + d && _up[_max + k] <= _down[_max + k])
                    {
                        mx = _down[_max + k];
                        my = mx - k;
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
