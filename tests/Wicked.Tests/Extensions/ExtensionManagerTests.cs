using System.Diagnostics;
using Wicked.Core;
using Wicked.Core.Extensions;
using Wicked.Core.Lsp;
using Xunit.Sdk;

namespace Wicked.Tests.Extensions;

public sealed class ExtensionManagerTests
{
    private static ExtensionManager Manager(ScratchFolder scratch, FakeBridge bridge, NoticeBoard? notices = null, HttpClient? http = null, Uri? api = null) =>
        new(scratch.Sub("extensions"), bridge, notices ?? new NoticeBoard(), http: http, githubApi: api);

    private static async Task WaitFor(Func<bool> condition, string what, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new XunitException("Timed out waiting for " + what);
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static EditorSnapshot Doc(string id, string language, string text = "hello world") =>
        new(id, id, language, 1, text, [new TextRange(0, 0)], false);

    [Fact]
    public async Task AnInstalledExtensionRunsItsCommandAndDecoratesADocument()
    {
        using var scratch = new ScratchFolder();
        var notices = new NoticeBoard();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, notices);
        var source = ExtensionKit.HelloFolder(scratch);
        var installed = await manager.InstallFromFolderAsync(source, ExtensionKit.Yes, TestContext.Current.CancellationToken);

        Assert.NotNull(installed);
        Assert.Equal(ExtensionState.Active, installed.State);
        Assert.True(installed.LoadMilliseconds > 0);
        Assert.Contains("command Say Hello", string.Join('|', installed.Provides), StringComparison.Ordinal);
        Assert.Equal("Hello: Say Hello", bridge.Commands["hello.say"].Title);
        Assert.Equal("Ctrl+Alt+H", bridge.Commands["hello.say"].Keys);

        await bridge.RunCommandAsync("hello.say");
        Assert.Equal("Hello from the extension", notices.Top?.Text);
        Assert.Equal(NoticeKind.Success, notices.Top?.Kind);

        bridge.RaiseOpened(Doc("/a.txt", "plaintext"));
        await WaitFor(() => bridge.Decorated.Count > 0, "the extension's decoration");
        var (owner, document, layer, marks) = bridge.Decorated[0];
        Assert.Equal(("acme.hello", "/a.txt", "hello"), (owner, document, layer));
        Assert.Equal(new RangeMark(0, 5, MarkStyle.Warning), Assert.Single(marks.Ranges!));
    }

    [Fact]
    public async Task DisablingTakesEverythingBackAndTheLoadContextIsCollected()
    {
        using var scratch = new ScratchFolder();
        var notices = new NoticeBoard();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, notices);
        await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        await bridge.RunCommandAsync("hello.say");
        Assert.Contains("hello.say", bridge.CommandIds());
        var handlers = bridge.HandlerCount;
        Assert.True(handlers > 0);

        await manager.SetEnabledAsync("acme.hello", false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain("hello.say", bridge.CommandIds());
        Assert.Equal(0, bridge.HandlerCount - 2); // only the manager's own two subscriptions remain
        Assert.Equal(ExtensionState.Disabled, manager.Installed.Single().State);
        Assert.Null(notices.All.FirstOrDefault(n => n.Key == "ext:acme.hello:hello"));
        Assert.True(await GcProbe.CollectedAsync(manager.UnloadProbe("acme.hello")), "the extension's load context was not collected");

        await manager.SetEnabledAsync("acme.hello", true, TestContext.Current.CancellationToken);
        Assert.Contains("hello.say", bridge.CommandIds());
        Assert.Equal(ExtensionState.Active, manager.Installed.Single().State);
    }

    [Fact]
    public async Task DisabledStaysDisabledAfterARestart()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using (var first = Manager(scratch, bridge))
        {
            await first.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes, TestContext.Current.CancellationToken);
            await first.SetEnabledAsync("acme.hello", false, TestContext.Current.CancellationToken);
        }

        var again = new FakeBridge();
        await using var second = Manager(scratch, again);
        await second.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionState.Disabled, second.Installed.Single().State);
        Assert.Empty(again.CommandIds());
    }

    [Fact]
    public async Task UninstallRemovesTheFolderAndUnloads()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        var folder = manager.Installed.Single().Folder;
        Assert.True(Directory.Exists(folder));

        await manager.UninstallAsync("acme.hello", TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(folder));
        Assert.Empty(manager.Installed);
        Assert.Empty(bridge.CommandIds());
        Assert.True(await GcProbe.CollectedAsync(manager.UnloadProbe("acme.hello")));
    }

    [Fact]
    public async Task AnExtensionWhoseActivationThrowsIsSwitchedOffWithANoticeAndTheOthersGoOn()
    {
        using var scratch = new ScratchFolder();
        var notices = new NoticeBoard();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, notices);
        var broken = ExtensionKit.HelloFolder(scratch, "broken-src", id: "broken", source: """
            using System.Threading;
            using System.Threading.Tasks;
            using Wicked.Core.Extensions;
            public sealed class Broken : IWickedExtension
            {
                public Task ActivateAsync(IWickedApi api, CancellationToken ct)
                {
                    api.Commands.Register("broken.half", "Half", () => { });
                    throw new System.InvalidOperationException("cannot start");
                }
            }
            """);
        await manager.InstallFromFolderAsync(broken, ExtensionKit.Yes, TestContext.Current.CancellationToken);
        await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes, TestContext.Current.CancellationToken);

        var failed = manager.Installed.Single(i => i.Id == "acme.broken");
        Assert.Equal(ExtensionState.Failed, failed.State);
        Assert.Contains("cannot start", failed.Problem);
        Assert.Contains(notices.All, n => n.Key == "ext:acme.broken:failed" && n.Kind == NoticeKind.Error && n.Text.Contains("cannot start", StringComparison.Ordinal));
        Assert.DoesNotContain("broken.half", bridge.CommandIds()); // what it registered before throwing is taken back
        Assert.Equal(ExtensionState.Active, manager.Installed.Single(i => i.Id == "acme.hello").State);
        Assert.Contains("hello.say", bridge.CommandIds());
    }

    [Fact]
    public async Task ACommandThatThrowsReportsToOutputAndTheExtensionKeepsRunning()
    {
        using var scratch = new ScratchFolder();
        var notices = new NoticeBoard();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, notices);
        var folder = ExtensionKit.HelloFolder(scratch, id: "thrower", source: """
            using System.Threading;
            using System.Threading.Tasks;
            using Wicked.Core.Extensions;
            public sealed class Thrower : IWickedExtension
            {
                public Task ActivateAsync(IWickedApi api, CancellationToken ct)
                {
                    api.Commands.Register("thrower.go", "Go", () => throw new System.InvalidOperationException("boom"));
                    return Task.CompletedTask;
                }
            }
            """);
        await manager.InstallFromFolderAsync(folder, ExtensionKit.Yes, TestContext.Current.CancellationToken);

        await bridge.RunCommandAsync("thrower.go");

        Assert.Contains(bridge.Output, l => l.Contains("boom", StringComparison.Ordinal) && l.StartsWith("Extensions:", StringComparison.Ordinal));
        Assert.Contains(notices.All, n => n.Key == "ext:acme.thrower:error");
        Assert.Equal(ExtensionState.Active, manager.Installed.Single().State);
    }

    [Fact]
    public async Task AnExtensionThatNeverFinishesActivatingIsFailedNotWaitedOnForever()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        // The real timeout is 30 s; the stand-in checks the cancellation path with a token that the host cancels on disposal.
        var folder = ExtensionKit.HelloFolder(scratch, id: "sleepy", source: """
            using System.Threading;
            using System.Threading.Tasks;
            using Wicked.Core.Extensions;
            public sealed class Sleepy : IWickedExtension
            {
                public async Task ActivateAsync(IWickedApi api, CancellationToken ct) => await Task.Delay(Timeout.Infinite, ct);
            }
            """);
        var install = manager.InstallFromFolderAsync(folder, ExtensionKit.Yes, TestContext.Current.CancellationToken);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.False(install.IsCompleted);
        await manager.DisposeAsync(); // cancels the stop token: the activation unwinds
        var result = await install.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : null, TaskScheduler.Default);
        Assert.True(result is null || result.State is ExtensionState.Failed or ExtensionState.Inactive);
    }

    [Fact]
    public async Task ADeclaredExtensionReachesTheRegistriesWithoutAnyCode()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        var folder = scratch.Sub("decl-src");
        Directory.CreateDirectory(Path.Combine(folder, "themes"));
        File.WriteAllText(Path.Combine(folder, "themes", "dusk.json"), "{ \"name\": \"Dusk\", \"colors\": {} }");
        File.WriteAllText(Path.Combine(folder, "foo.tmLanguage.json"), "{ \"scopeName\": \"source.foo\", \"patterns\": [] }");
        File.WriteAllText(Path.Combine(folder, "foo-snippets.json"), "{ \"Hi\": { \"prefix\": \"hi\", \"body\": [\"hello\"] } }");
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), ExtensionKit.Manifest(id: "pack", main: null, contributes: """
            { "themes": [ { "label": "Dusk", "path": "themes/dusk.json" } ],
              "grammars": [ { "language": "foo", "scopeName": "source.foo", "path": "foo.tmLanguage.json", "extensions": [".foo"] } ],
              "snippets": [ { "language": "foo", "path": "foo-snippets.json" } ],
              "languageServers": [ { "id": "foo-ls", "name": "Foo", "languages": ["foo"], "programs": ["./bin/foo-ls"], "arguments": ["--stdio"] } ],
              "keybindings": [ { "command": "workbench.action.files.save", "key": "ctrl+alt+s" } ],
              "settings": { "pack.greeting": "Hi" } }
            """));

        var installed = await manager.InstallFromFolderAsync(folder, ExtensionKit.Yes, TestContext.Current.CancellationToken);

        Assert.Equal(ExtensionState.Active, installed!.State);
        Assert.False(installed.RunsCode);
        Assert.EndsWith("dusk.json", Assert.Single(bridge.Themes), StringComparison.Ordinal);
        Assert.Equal(("foo", "source.foo"), (bridge.Grammars.Single().LanguageId, bridge.Grammars.Single().Scope));
        Assert.EndsWith("foo.tmLanguage.json", bridge.Grammars.Single().Path, StringComparison.Ordinal);
        Assert.EndsWith("foo-snippets.json", Assert.Single(bridge.Snippets), StringComparison.Ordinal);
        Assert.Contains("workbench.action.files.save=ctrl+alt+s", bridge.Keybindings);
        var server = LanguageServerCatalog.ById("foo-ls");
        Assert.NotNull(server);
        Assert.True(Path.IsPathRooted(server.Programs[0]));
        Assert.Contains(LanguageServerCatalog.For("foo"), s => s.Id == "foo-ls");
        Assert.Equal("Hi", manager.Registry.SettingDefault("pack.greeting")?.GetString());

        await manager.SetEnabledAsync("acme.pack", false, TestContext.Current.CancellationToken);

        Assert.Empty(bridge.Themes);
        Assert.Empty(bridge.Grammars);
        Assert.Empty(bridge.Snippets);
        Assert.Empty(bridge.Keybindings);
        Assert.Null(LanguageServerCatalog.ById("foo-ls"));
        Assert.Null(manager.Registry.SettingDefault("pack.greeting"));
    }

    [Fact]
    public async Task AnExtensionActivatesWhenItsLanguageOpens()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch, activation: ["onLanguage:foo"]), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionState.Inactive, manager.Installed.Single().State);
        Assert.Empty(bridge.CommandIds());

        bridge.RaiseOpened(Doc("/x.txt", "plaintext"));
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionState.Inactive, manager.Installed.Single().State);

        bridge.RaiseOpened(Doc("/x.foo", "foo"));
        await WaitFor(() => manager.Installed.Single().State == ExtensionState.Active, "activation on the language");
        Assert.Contains("hello.say", bridge.CommandIds());
    }

    [Fact]
    public async Task ReinstallingANewerVersionReplacesTheOlder()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch, "v1", "1.0.0"), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        var second = await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch, "v2", "1.1.0"), ExtensionKit.Yes, TestContext.Current.CancellationToken);

        Assert.Equal("1.1.0", second!.Manifest!.Version);
        Assert.Single(manager.Installed);
        Assert.Equal(["acme.hello-1.1.0"], Directory.GetDirectories(manager.Folder).Select(Path.GetFileName).Where(n => !n!.StartsWith('.')));
        Assert.Contains("hello.say", bridge.CommandIds());
    }

    [Fact]
    public async Task ARefusedInstallLeavesNothingBehind()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        InstallRequest? asked = null;

        var result = await manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), r =>
        {
            asked = r;
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal("acme.hello", asked!.Manifest.FullId);
        Assert.Empty(manager.Installed);
        Assert.Empty(bridge.CommandIds());
        Assert.Empty(Directory.GetDirectories(manager.Folder));
    }

    [Fact]
    public async Task AFolderForAnotherApiVersionCannotBeInstalled()
    {
        using var scratch = new ScratchFolder();
        await using var manager = Manager(scratch, new FakeBridge());
        var folder = ExtensionKit.HelloFolder(scratch);
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), ExtensionKit.Manifest(engine: "^2.0"));

        var thrown = await Assert.ThrowsAsync<ExtensionInstallException>(() => manager.InstallFromFolderAsync(folder, ExtensionKit.Yes, TestContext.Current.CancellationToken));
        Assert.Contains("^2.0", thrown.Message);
        Assert.Empty(manager.Installed);
    }

    [Fact]
    public async Task AZipInstallsWhetherItsFilesAreAtTheTopOrInOneFolder()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge);
        var folder = ExtensionKit.HelloFolder(scratch);

        var top = await manager.InstallFromZipAsync(ExtensionKit.Zip(folder, Path.Combine(scratch.Root, "top.wicked.zip")), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionState.Active, top!.State);
        await manager.UninstallAsync("acme.hello", TestContext.Current.CancellationToken);

        var wrapped = await manager.InstallFromZipAsync(ExtensionKit.Zip(folder, Path.Combine(scratch.Root, "wrapped.wicked.zip"), wrapInFolder: true), ExtensionKit.Yes, TestContext.Current.CancellationToken);
        Assert.Equal(ExtensionState.Active, wrapped!.State);
        Assert.Contains("hello.say", bridge.CommandIds());
    }

    [Fact]
    public async Task AZipThatEscapesItsFolderIsRefused()
    {
        using var scratch = new ScratchFolder();
        await using var manager = Manager(scratch, new FakeBridge());
        var zipPath = Path.Combine(scratch.Root, "evil.wicked.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("../../escaped.txt");
            await using var stream = entry.Open();
            await stream.WriteAsync("x"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        await Assert.ThrowsAnyAsync<IOException>(() => manager.InstallFromZipAsync(zipPath, ExtensionKit.Yes, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(manager.Folder)); // ../../ from the staging folder is the extensions folder itself
        Assert.Empty(manager.Installed);
    }

    [Fact]
    public async Task AGitHubReleaseInstallsWhenItsChecksumMatches()
    {
        using var scratch = new ScratchFolder();
        var zipPath = ExtensionKit.Zip(ExtensionKit.HelloFolder(scratch), Path.Combine(scratch.Root, "hello.wicked.zip"));
        using var github = new FakeGitHub("acme/hello", "hello.wicked.zip", File.ReadAllBytes(zipPath), "sha256:" + ExtensionKit.Sha256(zipPath));
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, http: new HttpClient(), api: github.BaseAddress);
        InstallRequest? asked = null;

        var installed = await manager.InstallFromGitHubAsync("acme/hello", r =>
        {
            asked = r;
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);

        Assert.True(asked!.Verified);
        Assert.Equal("github.com/acme/hello", asked.Source);
        Assert.Equal(ExtensionState.Active, installed!.State);
    }

    [Fact]
    public async Task AGitHubReleaseWithTheWrongChecksumIsRefusedAndNothingIsInstalled()
    {
        using var scratch = new ScratchFolder();
        var zipPath = ExtensionKit.Zip(ExtensionKit.HelloFolder(scratch), Path.Combine(scratch.Root, "hello.wicked.zip"));
        using var github = new FakeGitHub("acme/hello", "hello.wicked.zip", File.ReadAllBytes(zipPath), "sha256:" + new string('0', 64));
        var bridge = new FakeBridge();
        await using var manager = Manager(scratch, bridge, http: new HttpClient(), api: github.BaseAddress);
        var asked = false;

        var thrown = await Assert.ThrowsAsync<ExtensionInstallException>(() => manager.InstallFromGitHubAsync("acme/hello", _ =>
        {
            asked = true;
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken));

        Assert.Contains("checksum", thrown.Message, StringComparison.Ordinal);
        Assert.False(asked);
        Assert.Empty(manager.Installed);
        Assert.Empty(bridge.CommandIds());
        Assert.Empty(Directory.GetDirectories(manager.Folder));
    }

    [Fact]
    public async Task AGitHubReleaseWithoutAChecksumAsksWithVerifiedFalse()
    {
        using var scratch = new ScratchFolder();
        var zipPath = ExtensionKit.Zip(ExtensionKit.HelloFolder(scratch), Path.Combine(scratch.Root, "hello.wicked.zip"));
        using var github = new FakeGitHub("acme/hello", "hello.wicked.zip", File.ReadAllBytes(zipPath), digest: null);
        await using var manager = Manager(scratch, new FakeBridge(), http: new HttpClient(), api: github.BaseAddress);
        InstallRequest? asked = null;

        await manager.InstallFromGitHubAsync("acme/hello", r =>
        {
            asked = r;
            return Task.FromResult(false);
        }, TestContext.Current.CancellationToken);

        Assert.False(asked!.Verified);
    }

    [Theory]
    [InlineData("not a repo")]
    [InlineData("../../etc")]
    [InlineData("owner")]
    public async Task ARepositoryNameThatIsNotOwnerSlashNameIsRefusedBeforeAnyRequest(string name)
    {
        using var scratch = new ScratchFolder();
        await using var manager = Manager(scratch, new FakeBridge(), http: new HttpClient(), api: new Uri("http://127.0.0.1:1/"));
        await Assert.ThrowsAsync<ExtensionInstallException>(() => manager.InstallFromGitHubAsync(name, ExtensionKit.Yes, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AGitHubReleaseWithNoPackageAssetSaysSo()
    {
        using var scratch = new ScratchFolder();
        using var github = new FakeGitHub("acme/hello", "x", [], null, withAsset: false);
        await using var manager = Manager(scratch, new FakeBridge(), http: new HttpClient(), api: github.BaseAddress);
        var thrown = await Assert.ThrowsAsync<ExtensionInstallException>(() => manager.InstallFromGitHubAsync("acme/hello", ExtensionKit.Yes, TestContext.Current.CancellationToken));
        Assert.Contains("*.wicked.zip", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenFolderIsListedWithItsProblemAndCanBeUninstalled()
    {
        using var scratch = new ScratchFolder();
        var extensions = scratch.Sub("extensions");
        Directory.CreateDirectory(Path.Combine(extensions, "junk.thing-1.0.0"));
        File.WriteAllText(Path.Combine(extensions, "junk.thing-1.0.0", ExtensionManifest.FileName), "{ nope");
        await using var manager = new ExtensionManager(extensions, new FakeBridge(), new NoticeBoard());
        await manager.LoadAsync(TestContext.Current.CancellationToken);

        var broken = Assert.Single(manager.Installed);
        Assert.Equal(ExtensionState.Broken, broken.State);
        Assert.Contains("could not be read", broken.Problem);

        await manager.UninstallAsync(broken.Id, TestContext.Current.CancellationToken);
        Assert.Empty(manager.Installed);
    }

    [Fact]
    public async Task SettingsReadTheUsersValueThenTheManifestDefault()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        var notices = new NoticeBoard();
        var registry = new ExtensionRegistry();
        registry.SetDefaults("acme.hello", new Dictionary<string, System.Text.Json.JsonElement> { ["hello.greeting"] = System.Text.Json.JsonSerializer.SerializeToElement("Hi") });
        using var api = new WickedApi("acme.hello", scratch.Root, bridge, notices, registry);

        Assert.Equal("Hi", await api.Settings.GetAsync<string>("hello.greeting"));
        Assert.Equal("fallback", await api.Settings.GetAsync("nothing.here", "fallback"));
        await api.Settings.SetAsync("hello.greeting", "Hey");
        Assert.Equal("Hey", await api.Settings.GetAsync<string>("hello.greeting"));
        var changed = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.Settings.Changed += keys => changed.TrySetResult(keys);
        await api.Settings.SetAsync("hello.other", 3);
        Assert.Equal(["hello.other"], await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadingASmallExtensionIsFast()
    {
        using var scratch = new ScratchFolder();
        var bridge = new FakeBridge();
        var extensions = scratch.Sub("extensions");
        var folder = ExtensionKit.HelloFolder(scratch);
        double first;
        await using (var seed = new ExtensionManager(extensions, bridge, new NoticeBoard()))
        {
            var cold = Stopwatch.StartNew();
            await seed.InstallFromFolderAsync(folder, ExtensionKit.Yes, TestContext.Current.CancellationToken);
            first = cold.Elapsed.TotalMilliseconds;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"first install in this process (scan, copy, load, activate, JIT of the host's own code): {first:F1} ms");

        var times = new List<(double Total, double Load, double Activate)>();
        for (var i = 0; i < 5; i++)
        {
            await using var manager = new ExtensionManager(extensions, new FakeBridge(), new NoticeBoard());
            var clock = Stopwatch.StartNew();
            await manager.LoadAsync(TestContext.Current.CancellationToken);
            var total = clock.Elapsed.TotalMilliseconds;
            var info = manager.Installed.Single();
            Assert.Equal(ExtensionState.Active, info.State);
            times.Add((total, info.LoadMilliseconds, info.ActivateMilliseconds));
        }

        TestContext.Current.TestOutputHelper?.WriteLine("extension load times (total, assembly+type, ActivateAsync) ms: " + string.Join(" | ", times.Select(t => $"{t.Total:F1}, {t.Load:F1}, {t.Activate:F1}")));
        // The first load pays the JIT for the host's own loading code; the rest are what a start of the IDE costs.
        Assert.True(times.Skip(1).Min(t => t.Total) < 250, "a small extension should load in well under a quarter of a second");
    }
}
