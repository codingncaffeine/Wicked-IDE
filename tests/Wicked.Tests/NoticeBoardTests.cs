using Microsoft.Extensions.Time.Testing;
using Wicked.Core;

namespace Wicked.Tests;

public sealed class NoticeBoardTests
{
    [Fact]
    public void WorkUnderWayOutranksErrorsWhichOutrankThingsToActOn()
    {
        var board = new NoticeBoard();
        board.Post(new Notice("update", "A newer upload of a model is out", NoticeKind.Action, "panel/models"));
        board.Post(new Notice("saved", "Saved", NoticeKind.Info));
        board.Post(new Notice("failed", "A download failed", NoticeKind.Error));
        Assert.Equal("failed", board.Top?.Key);

        board.Post(new Notice("download", "Downloading", NoticeKind.Progress, Progress: 0.4));
        Assert.Equal("download", board.Top?.Key);
        Assert.Equal(["download", "failed", "update", "saved"], board.All.Select(n => n.Key));
    }

    [Fact]
    public void TheNewestWinsWithinAKindAndAKeyIsReplacedNotRepeated()
    {
        var board = new NoticeBoard();
        board.Post(new Notice("a", "first", NoticeKind.Action));
        board.Post(new Notice("b", "second", NoticeKind.Action));
        Assert.Equal("b", board.Top?.Key);

        board.Post(new Notice("a", "first, again", NoticeKind.Action));
        Assert.Equal(2, board.Count);
        Assert.Equal("first, again", board.Top?.Text);
    }

    [Fact]
    public void PassingWordsClearThemselvesButThingsToActOnStay()
    {
        var time = new FakeTimeProvider();
        var board = new NoticeBoard(time);
        board.Post(new Notice("saved", "Saved", NoticeKind.Info));
        board.Post(new Notice("update", "An update", NoticeKind.Action, "panel/models"));

        time.Advance(Notice.Dwell + TimeSpan.FromSeconds(1));
        Assert.True(SpinWait.SpinUntil(() => board.Count == 1, TimeSpan.FromSeconds(5)), "the passing word was not cleared");
        Assert.Equal("update", board.Top?.Key);

        board.Clear("update");
        Assert.Null(board.Top);
    }

    [Fact]
    public void ARepostedPassingWordKeepsItsOwnTime()
    {
        var time = new FakeTimeProvider();
        var board = new NoticeBoard(time);
        board.Post(new Notice("saved", "Saved", NoticeKind.Info));
        time.Advance(Notice.Dwell - TimeSpan.FromSeconds(1));
        board.Post(new Notice("saved", "Saved again", NoticeKind.Info));

        // The first posting's timer fires now; it must not take the second posting with it.
        time.Advance(TimeSpan.FromSeconds(2));
        Thread.Sleep(100);
        Assert.Equal("Saved again", board.Top?.Text);
    }
}
