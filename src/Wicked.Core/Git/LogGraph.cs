using System.Collections.Immutable;

namespace Wicked.Core.Git;

/// <summary>Which part of a graph row a line is drawn in: the whole height, the top half down to the commit's dot, or the bottom half from it.</summary>
public enum GraphSpan
{
    /// <summary>A branch passing by: from the row's top to its bottom in <c>FromLane</c> (= <c>ToLane</c>).</summary>
    Through,

    /// <summary>A line arriving at the dot from the row above: from the top of <c>FromLane</c> to the dot.</summary>
    Upper,

    /// <summary>A line leaving the dot to a parent below: from the dot to the bottom of <c>ToLane</c>.</summary>
    Lower,
}

/// <summary>One line of a graph row. <see cref="Color"/> is a lane colour index the view maps to its palette.</summary>
public readonly record struct GraphEdge(GraphSpan Span, int FromLane, int ToLane, int Color);

/// <summary>A commit's row: where its dot sits and the lines around it.</summary>
public sealed record GraphRow(GitCommit Commit, int Lane, int Color, ImmutableArray<GraphEdge> Edges, int LaneCount);

/// <summary>
/// Lays a log out in lanes. Feed it pages of commits in log order (children before parents); it remembers which lane waits for
/// which parent, so the next page continues the same picture. A commit takes the lane that was waiting for it, its first
/// parent keeps that lane, and further parents get the lane already waiting for them or a new one. Lanes are reused as soon as
/// they free up, and colours follow branches: a lane keeps its colour until its line ends.
/// </summary>
public sealed class LogGraphBuilder
{
    private const int Palette = 8;
    private readonly List<(string? Waiting, int Color)> _lanes = [];
    private int _nextColor;

    public ImmutableArray<GraphRow> Add(IEnumerable<GitCommit> commits)
    {
        var rows = ImmutableArray.CreateBuilder<GraphRow>();
        foreach (var commit in commits) rows.Add(Row(commit));
        return rows.ToImmutable();
    }

    private GraphRow Row(GitCommit commit)
    {
        var edges = new List<GraphEdge>();
        var arriving = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i].Waiting == commit.Hash) arriving.Add(i);
        }

        int lane;
        int color;
        if (arriving.Count > 0)
        {
            lane = arriving[0];
            color = _lanes[lane].Color;
        }
        else
        {
            lane = FreeLane();
            color = NewColor();
            _lanes[lane] = (commit.Hash, color);
        }

        // The upper half: every lane that waited for this commit joins its dot; the others pass by.
        foreach (var i in arriving)
        {
            edges.Add(new GraphEdge(GraphSpan.Upper, i, lane, _lanes[i].Color));
        }

        var passing = new List<int>();
        for (var i = 0; i < _lanes.Count; i++)
        {
            if (_lanes[i].Waiting is not null && i != lane && !arriving.Contains(i)) passing.Add(i);
        }

        // The commit is placed: its lanes are free until its parents claim them.
        foreach (var i in arriving) _lanes[i] = (null, _lanes[i].Color);
        var drawnBefore = _lanes.Count;
        var lower = new List<GraphEdge>();
        if (commit.Parents.Length > 0)
        {
            _lanes[lane] = (commit.Parents[0], color);
            lower.Add(new GraphEdge(GraphSpan.Lower, lane, lane, color));
            foreach (var parent in commit.Parents.Skip(1))
            {
                var existing = _lanes.FindIndex(l => l.Waiting == parent);
                if (existing < 0)
                {
                    existing = FreeLane();
                    _lanes[existing] = (parent, NewColor());
                }

                lower.Add(new GraphEdge(GraphSpan.Lower, lane, existing, _lanes[existing].Color));
            }
        }
        else
        {
            _lanes[lane] = (null, color);
        }

        foreach (var i in passing) edges.Add(new GraphEdge(GraphSpan.Through, i, i, _lanes[i].Color));
        edges.AddRange(lower);
        var width = Math.Max(drawnBefore, _lanes.Count);

        // Trailing empty lanes are dropped so the graph narrows when branches end.
        while (_lanes.Count > 0 && _lanes[^1].Waiting is null) _lanes.RemoveAt(_lanes.Count - 1);
        return new GraphRow(commit, lane, color, [.. edges], Math.Max(width, lane + 1));
    }

    private int FreeLane()
    {
        var free = _lanes.FindIndex(l => l.Waiting is null);
        if (free >= 0) return free;
        _lanes.Add((null, 0));
        return _lanes.Count - 1;
    }

    private int NewColor() => _nextColor++ % Palette;
}
