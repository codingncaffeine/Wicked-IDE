using System.Text.Json;
using Wicked.Core.Debug;
using Wicked.Core.Settings;

namespace Wicked.Tests.Debug;

public sealed class BreakpointStoreTests
{
    private const string File1 = "/work/app/Program.cs";
    private const string File2 = "/work/app/Other.cs";

    [Fact]
    public void ToggleAddsABreakpointAndTheSecondToggleRemovesIt()
    {
        var store = new BreakpointStore();
        var changes = new List<BreakpointsChanged>();
        store.Changed += changes.Add;

        var added = store.Toggle(File1, 10);
        var removed = store.Toggle(File1, 10);

        Assert.NotNull(added);
        Assert.Null(removed);
        Assert.Empty(store.Source);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal([File1], c.Files));
    }

    [Fact]
    public void AddingOnALineThatHasOneReturnsTheExistingBreakpoint()
    {
        var store = new BreakpointStore();

        var first = store.Add(File1, 4);
        var again = store.Add(File1, 4, condition: "x > 1");

        Assert.Equal(first, again);
        Assert.Single(store.Source);
        Assert.Null(store.Find(first.Id)!.Condition);
    }

    [Fact]
    public void ForFileListsOnlyThatFilesBreakpointsInLineOrderAndPathsAreNormalised()
    {
        var store = new BreakpointStore();
        store.Add(File1, 30);
        store.Add(File2, 5);
        store.Add("/work/app/../app/Program.cs", 12);

        Assert.Equal([12, 30], store.ForFile(File1).Select(b => b.Line));
        Assert.Equal([5], store.ForFile(File2).Select(b => b.Line));
        Assert.NotNull(store.At(File1, 12));
    }

    [Fact]
    public void KindsFollowWhatIsSetOnThem()
    {
        var store = new BreakpointStore();
        var plain = store.Add(File1, 1);
        var conditional = store.Add(File1, 2, condition: "i == 3");
        var counted = store.Add(File1, 3, hitCondition: "> 5");
        var log = store.Add(File1, 4, logMessage: "i = {i}");

        Assert.Equal(BreakpointKind.Plain, plain.Kind);
        Assert.Equal(BreakpointKind.Conditional, conditional.Kind);
        Assert.Equal(BreakpointKind.Conditional, counted.Kind);
        Assert.Equal(BreakpointKind.Logpoint, log.Kind);
        Assert.Equal("i == 3", conditional.Summary);
        Assert.Equal("hit count > 5", counted.Summary);
        Assert.Equal("Log: i = {i}", log.Summary);
    }

    [Fact]
    public void UpdateKeepsTheIdBlanksBecomeNothingAndNoChangeRaisesNothing()
    {
        var store = new BreakpointStore();
        var bp = store.Add(File1, 7);
        var raised = 0;
        store.Changed += _ => raised++;

        store.Update(bp.Id, b => b with { Condition = "   ", Enabled = false });
        store.Update(bp.Id, b => b with { Enabled = false });
        store.Update(bp.Id, b => b with { Condition = "n > 2", Id = 999 });

        var now = store.Find(bp.Id)!;
        Assert.Equal(2, raised);
        Assert.Equal("n > 2", now.Condition);
        Assert.False(now.Enabled);
        Assert.Null(store.Find(999));
    }

    [Fact]
    public void RelocateMovesBreakpointsDropsTheMissingAndKeepsOneOfTwoOnALine()
    {
        var store = new BreakpointStore();
        var a = store.Add(File1, 5);
        var b = store.Add(File1, 8);
        var c = store.Add(File1, 9);
        var other = store.Add(File2, 5);

        store.Relocate(File1, new Dictionary<int, int> { [a.Id] = 6, [c.Id] = 6 });

        var left = store.ForFile(File1);
        Assert.Equal(a.Id, Assert.Single(left).Id);
        Assert.Equal(6, left[0].Line);
        Assert.Null(store.Find(b.Id));
        Assert.Equal(5, store.Find(other.Id)!.Line);
    }

    [Fact]
    public void SetAllEnabledAndRemoveAllReachFunctionBreakpointsToo()
    {
        var store = new BreakpointStore();
        store.Add(File1, 1);
        store.AddFunction("Main");

        store.SetAllEnabled(false);
        Assert.All(store.Source, b => Assert.False(b.Enabled));
        Assert.All(store.Functions, f => Assert.False(f.Enabled));

        store.RemoveAll();
        Assert.Empty(store.Source);
        Assert.Empty(store.Functions);
    }

    [Fact]
    public void ExceptionFiltersKeepTheUsersChoiceWhenAnAdapterOffersThemAgain()
    {
        var store = new BreakpointStore();
        var offered = new[] { new ExceptionBreakpointsFilter("all", "All Exceptions"), new ExceptionBreakpointsFilter("uncaught", "Uncaught Exceptions", Default: true) };
        store.OfferFilters(offered);
        store.SetFilter("all", true);
        store.SetFilter("uncaught", false);

        store.OfferFilters(offered);
        store.OfferFilters([new ExceptionBreakpointsFilter("raised", "Raised", Default: true)]);

        var filters = store.Filters.ToDictionary(f => f.Filter);
        Assert.True(filters["all"].Enabled);
        Assert.False(filters["uncaught"].Enabled);
        Assert.True(filters["raised"].Enabled);
    }

    [Fact]
    public void WhatTheStoreRemembersSurvivesTheSessionFileAndKeepsIdsUnique()
    {
        var store = new BreakpointStore();
        store.Add(File1, 3, condition: "a < b");
        store.Add(File2, 9, logMessage: "hit");
        store.AddFunction("Main");
        store.OfferFilters([new ExceptionBreakpointsFilter("all", "All Exceptions")]);
        store.SetFilter("all", true);
        var json = JsonSerializer.Serialize(new SessionState { Debug = new() { ["/work/app"] = store.Snapshot(["a + b"], "Launch") } }, WickedSettings.Json);

        var back = JsonSerializer.Deserialize<SessionState>(json, WickedSettings.Json)!.Debug["/work/app"];
        var restored = new BreakpointStore();
        restored.Restore(back);
        var fresh = restored.Add(File1, 100);

        Assert.Equal(["a < b"], restored.ForFile(File1).Where(b => b.Line == 3).Select(b => b.Condition));
        Assert.Equal("hit", restored.ForFile(File2)[0].LogMessage);
        Assert.Equal("Main", restored.Functions[0].Name);
        Assert.True(restored.Filters[0].Enabled);
        Assert.Equal(["a + b"], back.Watches);
        Assert.Equal("Launch", back.Configuration);
        Assert.DoesNotContain(restored.Source.Where(b => b != fresh), b => b.Id == fresh.Id);
    }
}
