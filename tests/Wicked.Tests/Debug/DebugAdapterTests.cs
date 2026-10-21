using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Debug;
using Wicked.Core.Http;
using Wicked.Core.Lsp;

namespace Wicked.Tests.Debug;

/// <summary>The adapter catalog, finding an installed adapter, installing one from a release, and choosing one for a configuration.</summary>
public sealed class DebugAdapterTests : IDisposable
{
    private readonly TempProfile _profile = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _profile.Dispose();

    [Fact]
    public void TheCatalogCoversTheFourAdaptersAndEveryTypeLeadsToOne()
    {
        Assert.Equal(["netcoredbg", "codelldb", "debugpy", "js-debug"], DebugAdapterCatalog.All.Take(4).Select(a => a.Id));
        Assert.Equal("netcoredbg", DebugAdapterCatalog.ForType("coreclr").First().Id);
        Assert.Equal("codelldb", DebugAdapterCatalog.ForType("lldb").First().Id);
        Assert.Equal("debugpy", DebugAdapterCatalog.ForType("python").First().Id);
        Assert.Equal("js-debug", DebugAdapterCatalog.ForType("pwa-node").First().Id);
        Assert.Contains("codelldb", DebugAdapterCatalog.ForLanguage("rust").Select(a => a.Id));
        Assert.Equal("netcoredbg", DebugAdapterCatalog.ForLanguage("csharp").First().Id);
        Assert.Empty(DebugAdapterCatalog.ForType("cppdbg"));
    }

    [Fact]
    public void EveryReleaseAdapterHasABuildForThisPlatformAndATcpAdapterAsksForAPort()
    {
        foreach (var spec in DebugAdapterCatalog.All.Where(a => a.Install == AdapterInstall.GitHubRelease))
        {
            Assert.NotNull(spec.Repository);
            Assert.Contains(spec.Assets!, a => a.Platform == "linux-x64");
            Assert.All(spec.Assets!, a => Assert.NotEmpty(a.Program));
        }

        foreach (var spec in DebugAdapterCatalog.All.Where(a => a.Transport == AdapterTransport.Tcp)) Assert.Contains(spec.Arguments, a => a.Contains("{port}", StringComparison.Ordinal));
    }

    private string Bin(string id, string name, string body = "#!/bin/sh\n")
    {
        var folder = Path.Combine(_profile.Root, "adapters", id, "bin");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void AnInstalledAdapterWinsOverOneOnThePathAndTheOtherWayRoundIsFoundToo()
    {
        var onPath = Path.Combine(_profile.Root, "path");
        Directory.CreateDirectory(onPath);
        File.WriteAllText(Path.Combine(onPath, "netcoredbg"), "#!/bin/sh\n");
        var locator = new DebugAdapterLocator(Path.Combine(_profile.Root, "adapters"), n => n == "PATH" ? onPath : null);
        var spec = DebugAdapterCatalog.ById("netcoredbg")!;

        var fromPath = locator.Find(spec)!;
        var installed = Bin("netcoredbg", "netcoredbg");
        var fromFolder = locator.Find(spec)!;

        Assert.Equal(Path.Combine(onPath, "netcoredbg"), fromPath.Program);
        Assert.Equal(installed, fromFolder.Program);
        Assert.Equal(["--interpreter=vscode"], fromFolder.Arguments);
        Assert.Equal(AdapterTransport.Stdio, fromFolder.Transport);
        Assert.Null(new DebugAdapterLocator(Path.Combine(_profile.Root, "none"), _ => null).Find(spec));
    }

    [Fact]
    public void JsDebugRunsUnderNodeAndDebugpyUnderPythonWithItsOwnFolderOnThePath()
    {
        var path = Path.Combine(_profile.Root, "path");
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "node"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(path, "python3"), "#!/bin/sh\n");
        var locator = new DebugAdapterLocator(Path.Combine(_profile.Root, "adapters"), n => n == "PATH" ? path : null);
        var js = DebugAdapterCatalog.ById("js-debug")!;
        var py = DebugAdapterCatalog.ById("debugpy")!;

        Assert.Null(locator.Find(js));
        Assert.Null(locator.Find(py));
        var script = Bin("js-debug", "dapDebugServer.js");
        Directory.CreateDirectory(Path.Combine(_profile.Root, "adapters", "debugpy", "lib"));

        var node = locator.Find(js)!;
        var python = locator.Find(py)!;

        Assert.Equal(Path.Combine(path, "node"), node.Program);
        Assert.Equal([script, "{port}", "127.0.0.1"], node.Arguments);
        Assert.Equal(AdapterTransport.Tcp, node.Transport);
        Assert.Equal(Path.Combine(path, "python3"), python.Program);
        Assert.Equal(["-m", "debugpy.adapter"], python.Arguments);
        Assert.Equal(Path.Combine(_profile.Root, "adapters", "debugpy", "lib"), python.Environment["PYTHONPATH"]);
    }

    [Fact]
    public async Task AMissingAdapterIsAnErrorThatNamesTheAdapterThatWouldServeTheType()
    {
        var provider = new DebugAdapterProvider(new DebugAdapterLocator(Path.Combine(_profile.Root, "adapters"), _ => null));

        var missing = await Assert.ThrowsAsync<DebugAdapterMissingException>(() => provider.ResolveAsync("coreclr", default, Ct));
        var unknown = await Assert.ThrowsAsync<DebugAdapterMissingException>(() => provider.ResolveAsync("cppdbg", default, Ct));

        Assert.Equal("netcoredbg", missing.Spec!.Id);
        Assert.Contains("netcoredbg is not installed", missing.Message);
        Assert.Null(unknown.Spec);
        Assert.Contains("cppdbg", unknown.Message);
    }

    [Fact]
    public async Task ADebugServerPortInTheConfigurationMeansConnectingToIt()
    {
        var provider = new DebugAdapterProvider(new DebugAdapterLocator(Path.Combine(_profile.Root, "adapters"), _ => null));

        var endpoint = await provider.ResolveAsync("anything", JsonDocument.Parse("""{"debugServer": 4711}""").RootElement, Ct);

        Assert.IsType<TcpAdapterEndpoint>(endpoint);
    }

    // ── Installing ───────────────────────────────────────────────────────────────────────────────

    private static byte[] Archive()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "fakedbg/") { Mode = (UnixFileMode)0b111_101_101 });
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "fakedbg/fakedbg") { Mode = (UnixFileMode)0b111_101_101, DataStream = new MemoryStream(Encoding.UTF8.GetBytes("#!/bin/sh\necho adapter\n")) });
        }

        return buffer.ToArray();
    }

    private static DebugAdapterSpec Fake() => new("fakedbg", "Fake debugger", ["fake"], ["fakelang"], ["fakedbg"], ["--stdio"], AdapterTransport.Stdio, AdapterInstall.GitHubRelease,
        Repository: "example/fakedbg", Assets: [new(LanguageServerInstaller.Platform, "fakedbg-{version}.tar.gz", "tar.gz", "fakedbg/fakedbg")]);

    private async Task<LocalServer> ReleaseAsync(byte[] archive, string digest)
    {
        LocalServer? server = null;
        server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/repos/example/fakedbg/releases/latest", () => Results.Text(JsonSerializer.Serialize(new
            {
                tag_name = "v2.5.0",
                assets = new[] { new { name = "fakedbg-2.5.0.tar.gz", browser_download_url = server!.Url("/download/fakedbg.tar.gz").ToString(), digest = "sha256:" + digest } },
            }), "application/json"));
            app.MapGet("/download/fakedbg.tar.gz", () => Results.Bytes(archive));
        });
        return server;
    }

    [Fact]
    public async Task InstallingDownloadsTheReleaseChecksItsDigestAndLeavesALauncherTheLocatorFinds()
    {
        var archive = Archive();
        await using var server = await ReleaseAsync(archive, Convert.ToHexStringLower(SHA256.HashData(archive)));
        using var http = WickedHttp.CreateClient(offline: () => false);
        var folder = Path.Combine(_profile.Root, "adapters");
        var progress = new List<string>();

        await new DebugAdapterInstaller(http, folder, server.BaseAddress).InstallAsync(Fake(), new Progress<string>(progress.Add), Ct);

        var command = new DebugAdapterLocator(folder, _ => null).Find(Fake())!;
        Assert.Equal(Path.Combine(folder, "fakedbg", "bin", "fakedbg"), command.Program);
        Assert.Equal("v2.5.0\n", await File.ReadAllTextAsync(Path.Combine(folder, "fakedbg", "version.txt"), Ct));
        if (!OperatingSystem.IsWindows()) Assert.True((File.GetUnixFileMode(command.Program) & UnixFileMode.UserExecute) != 0);
        Assert.Contains(progress, p => p.Contains("Downloading", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReleaseThatDoesNotMatchItsPublishedDigestIsRefusedAndNothingIsLeftInstalled()
    {
        var archive = Archive();
        await using var server = await ReleaseAsync(archive, new string('0', 64));
        using var http = WickedHttp.CreateClient(offline: () => false);
        var folder = Path.Combine(_profile.Root, "adapters");

        var ex = await Assert.ThrowsAsync<ServerInstallException>(() => new DebugAdapterInstaller(http, folder, server.BaseAddress).InstallAsync(Fake(), null, Ct));

        Assert.Contains("checksum", ex.Message);
        Assert.Null(new DebugAdapterLocator(folder, _ => null).Find(Fake()));
    }

    [Fact]
    public void AnAdapterThatNeedsARuntimeOrASystemPackageSaysWhatIsMissing()
    {
        var js = DebugAdapterCatalog.ById("js-debug")!;
        var gdb = DebugAdapterCatalog.ById("gdb")!;
        var py = DebugAdapterCatalog.ById("debugpy")!;

        Assert.Contains("node", DebugAdapterInstaller.Unavailable(js, _ => "")!);
        Assert.Contains("system's packages", DebugAdapterInstaller.Unavailable(gdb, _ => "")!);
        Assert.Contains("Python", DebugAdapterInstaller.Unavailable(py, _ => "")!);
        Assert.Null(DebugAdapterInstaller.Unavailable(DebugAdapterCatalog.ById("netcoredbg")!, _ => ""));
    }
}
