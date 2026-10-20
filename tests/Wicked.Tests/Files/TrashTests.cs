using Wicked.Core.Files;

namespace Wicked.Tests.Files;

public sealed class TrashTests
{
    [Fact]
    public async Task AFileGoesToTheTrashWithItsWayBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the freedesktop Trash");
        using var profile = new TempProfile();
        var data = Path.Combine(profile.Root, "xdg");
        string? Env(string v) => v == "XDG_DATA_HOME" ? data : null;
        var file = Path.Combine(profile.Root, "notes.txt");
        await File.WriteAllTextAsync(file, "keep me", TestContext.Current.CancellationToken);
        var again = Path.Combine(profile.Root, "again");
        Directory.CreateDirectory(again);
        await File.WriteAllTextAsync(Path.Combine(again, "notes.txt"), "me too", TestContext.Current.CancellationToken);

        await Trash.MoveToTrashAsync(file, Env, TestContext.Current.CancellationToken);
        await Trash.MoveToTrashAsync(Path.Combine(again, "notes.txt"), Env, TestContext.Current.CancellationToken);

        Assert.False(File.Exists(file));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(data, "Trash", "files", "notes.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("me too", await File.ReadAllTextAsync(Path.Combine(data, "Trash", "files", "notes.2.txt"), TestContext.Current.CancellationToken));
        var info = await File.ReadAllTextAsync(Path.Combine(data, "Trash", "info", "notes.txt.trashinfo"), TestContext.Current.CancellationToken);
        Assert.StartsWith("[Trash Info]\nPath=", info);
        Assert.Contains("notes.txt", info);
        Assert.Contains("DeletionDate=", info);
    }

    [Fact]
    public async Task AFolderGoesWhole()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the freedesktop Trash");
        using var profile = new TempProfile();
        var data = Path.Combine(profile.Root, "xdg");
        var folder = Path.Combine(profile.Root, "build out");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        await Trash.MoveToTrashAsync(folder, v => v == "XDG_DATA_HOME" ? data : null, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(Path.Combine(data, "Trash", "files", "build out", "sub")));
        Assert.Contains("build%20out", await File.ReadAllTextAsync(Path.Combine(data, "Trash", "info", "build out.trashinfo"), TestContext.Current.CancellationToken));
    }
}
