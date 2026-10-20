using Wicked.Core;
using Wicked.Daemon;

namespace Wicked.Tests;

public sealed class DaemonTests
{
    [Fact]
    public async Task AnswersHealthOnItsSocket()
    {
        using var profile = new TempProfile();
        await using var daemon = await DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken);
        using var client = new DaemonClient(profile.Paths.DaemonSocket);

        var health = await client.GetHealthAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(Environment.ProcessId, health.ProcessId);
        Assert.Equal(BuildInfo.Version, health.Version);
    }

    [Fact]
    public async Task ASecondDaemonForTheSameProfileStops()
    {
        using var profile = new TempProfile();
        await using var first = await DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<DaemonAlreadyRunningException>(() => DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken));

        // The first is still the one answering.
        using var client = new DaemonClient(profile.Paths.DaemonSocket);
        Assert.NotNull(await client.GetHealthAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ASocketLeftByADeadDaemonIsReplaced()
    {
        using var profile = new TempProfile();
        profile.Paths.EnsureCreated();
        await File.WriteAllTextAsync(profile.Paths.DaemonSocket, "left behind", TestContext.Current.CancellationToken);

        await using var daemon = await DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken);
        using var client = new DaemonClient(profile.Paths.DaemonSocket);

        Assert.NotNull(await client.GetHealthAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NoDaemonMeansNoHealthNotAnError()
    {
        using var profile = new TempProfile();
        using var client = new DaemonClient(profile.Paths.DaemonSocket, TimeSpan.FromSeconds(2));

        Assert.Null(await client.GetHealthAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StoppingRemovesTheSocketAndFreesTheProfile()
    {
        using var profile = new TempProfile();
        var first = await DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken);
        await first.DisposeAsync();
        Assert.False(File.Exists(profile.Paths.DaemonSocket));

        await using var second = await DaemonHost.StartAsync(profile.Paths, TestContext.Current.CancellationToken);
        using var client = new DaemonClient(profile.Paths.DaemonSocket);
        Assert.NotNull(await client.GetHealthAsync(TestContext.Current.CancellationToken));
    }
}
