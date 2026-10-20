using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Downloads;
using Wicked.Core.Engines;
using Wicked.Core.Hardware;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class EngineInstallerTests : IDisposable
{
    private const string FakeServer = """
        case "$1" in
          --version) echo "version: 0.7.0-dev (build 123, commit abc1234)" >&2; echo "built with fake" >&2 ;;
          --list-devices) echo "Available devices:"; echo "  CUDA0: NVIDIA GeForce RTX 5080 (16303 MiB, 14000 MiB free)" ;;
        esac
        """;

    private readonly TempProfile _profile = new();
    private readonly HttpClient _http = WickedHttp.CreateClient(offline: () => false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Engines => Path.Combine(_profile.Root, "engines");

    public void Dispose()
    {
        _http.Dispose();
        _profile.Dispose();
    }

    private static byte[] Tar(string top, Action<TarWriter, string> fill)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, top + "/") { Mode = (UnixFileMode)0b111_101_101 });
            fill(writer, top);
        }

        return buffer.ToArray();
    }

    private static void File_(TarWriter w, string name, string content, UnixFileMode mode)
    {
        w.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { Mode = mode, DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) });
    }

    private const UnixFileMode Exec = (UnixFileMode)0b111_101_101;
    private const UnixFileMode Plain = (UnixFileMode)0b110_100_100;

    private static byte[] EngineArchive(string tag, string serverBody = FakeServer) => Tar($"llama-{tag}", (w, top) =>
    {
        File_(w, $"{top}/llama-server", "#!/bin/sh\n" + serverBody + "\n", Exec);
        File_(w, $"{top}/libggml.so.0.27.0", "lib", Exec);
        w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, $"{top}/libggml.so.0") { LinkName = "libggml.so.0.27.0" });
        File_(w, $"{top}/LICENSE", "MIT", Plain);
    });

    private static byte[] CudartArchive(string tag) => Tar($"cudart-{tag}", (w, top) => File_(w, $"{top}/libcudart.so.13", "cudart", Exec));

    private async Task<(EngineInstaller Installer, EngineRelease Release, EngineChoice Choice, LocalServer Server, DownloadManager Downloads)> SetUpAsync(
        bool cuda, string tag = "b123", string serverBody = FakeServer, byte[]? mainBytes = null, string? wrongHash = null)
    {
        var main = mainBytes ?? EngineArchive(tag, serverBody);
        var runtime = CudartArchive(tag);
        var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/main.tar.gz", () => Results.Bytes(main));
            app.MapGet("/cudart.tar.gz", () => Results.Bytes(runtime));
        });
        string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));
        var suffix = cuda ? "ubuntu-cuda-13.4-x64" : "ubuntu-x64";
        var assets = new List<EngineAsset> { new($"llama-{tag}-bin-{suffix}.tar.gz", main.Length, server.Url("/main.tar.gz"), wrongHash ?? Sha(main)) };
        if (cuda) assets.Add(new($"cudart-llama-{tag}-bin-{suffix}.tar.gz", runtime.Length, server.Url("/cudart.tar.gz"), Sha(runtime)));
        var release = new EngineRelease(tag, int.Parse(tag[1..], System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.UtcNow, true, assets);
        var hardware = new HardwareInfo(new CpuInfo("c", 8, true, false), 8L << 30, 4L << 30,
            cuda ? [new GpuInfo(GpuVendor.Nvidia, "n", 16L << 30, 14L << 30, "615", new Version(13, 4), null)] : []);
        var choice = EngineSelector.Choose(hardware, new EnginePlatform(EngineOs.Linux, EngineArch.X64));
        var downloads = new DownloadManager(_http, Path.Combine(_profile.Root, "state", "downloads.json"),
            new DownloadOptions { MinFreeBytes = 0, BackoffBase = TimeSpan.FromMilliseconds(5), MaxAttempts = 1 });
        return (new EngineInstaller(downloads, Engines, Path.Combine(_profile.Root, "dl")), release, choice, server, downloads);
    }

    [Fact]
    public async Task InstallsACpuEngineAndKeepsSymlinksAndExecBits()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false);
        await using var _ = server;
        await using var __ = downloads;
        var reports = new List<EngineInstallProgress>();

        var install = await installer.InstallAsync(release, choice, new Progress<EngineInstallProgress>(reports.Add), ct: Ct);

        Assert.Equal("b123-cpu", install.Id);
        Assert.Equal(Path.Combine(Engines, "b123-cpu", "llama-server"), install.ServerPath);
        if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(install.ServerPath).HasFlag(UnixFileMode.UserExecute));
        var link = new FileInfo(Path.Combine(install.Directory, "libggml.so.0"));
        Assert.Equal("libggml.so.0.27.0", link.LinkTarget);
        Assert.Equal("lib", await File.ReadAllTextAsync(link.FullName, Ct));
        Assert.Equal(install.Id, installer.Current!.Id);
        Assert.True(File.Exists(Path.Combine(install.Directory, "engine.json")));
        Assert.Empty(Directory.GetDirectories(Engines, ".tmp-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(_profile.Root, "dl")));
        Assert.Contains(reports, r => r.Phase == EnginePhase.Downloading);
        Assert.Equal(EnginePhase.Done, reports[^1].Phase);

        var report = await EngineInstaller.ProbeAsync(install, Ct);
        Assert.Equal(123, report.Version!.Build);
        Assert.Equal("CUDA0", Assert.Single(report.Devices).Id);
    }

    [Fact]
    public async Task CudaEnginesGetTheirRuntimeMergedIn()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: true);
        await using var _ = server;
        await using var __ = downloads;

        var install = await installer.InstallAsync(release, choice, ct: Ct);

        Assert.Equal("b123-cuda-13.4", install.Id);
        Assert.Equal("cudart", await File.ReadAllTextAsync(Path.Combine(install.Directory, "libcudart.so.13"), Ct));
        Assert.True(File.Exists(install.ServerPath));
        Assert.NotNull(install.CudartAsset);
    }

    [Fact]
    public async Task KeepsThePreviousEngineAndPrunesOlderOnes()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        string[] ids = [];
        EngineInstaller? installer = null;
        foreach (var tag in new[] { "b101", "b102", "b103" })
        {
            var (inst, release, choice, server, downloads) = await SetUpAsync(cuda: false, tag);
            installer = inst;
            await using var _ = server;
            await using var __ = downloads;
            await installer.InstallAsync(release, choice, ct: Ct);
            ids = [.. installer.List().Select(i => i.Id).Order()];
        }

        Assert.Equal(["b102-cpu", "b103-cpu"], ids);
        Assert.Equal("b103-cpu", installer!.Current!.Id);
        Assert.Equal("b102-cpu", installer.Previous!.Id);

        var back = await installer.RollbackAsync();
        Assert.Equal("b102-cpu", back.Id);
        Assert.Equal("b102-cpu", installer.Current!.Id);
        Assert.Equal("b103-cpu", installer.Previous!.Id);
    }

    [Fact]
    public async Task InstallingTheSameEngineTwiceIsANoOp()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false);
        await using var _ = server;
        await using var __ = downloads;
        var first = await installer.InstallAsync(release, choice, ct: Ct);

        var second = await installer.InstallAsync(release, choice, ct: Ct);

        Assert.Equal(first.InstalledAt, second.InstalledAt);
    }

    [Fact]
    public async Task AWrongDigestFailsTheInstallAndLeavesNothingBehind()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false, wrongHash: new string('a', 64));
        await using var _ = server;
        await using var __ = downloads;

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => installer.InstallAsync(release, choice, ct: Ct));

        Assert.Contains("Checksum mismatch", ex.Message, StringComparison.Ordinal);
        Assert.Empty(installer.List());
        Assert.Null(installer.Current);
    }

    [Fact]
    public async Task AnEngineThatDoesNotStartIsNotInstalled()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false, serverBody: "echo 'libcuda.so.1: cannot open shared object file' >&2; exit 127");
        await using var _ = server;
        await using var __ = downloads;

        var ex = await Assert.ThrowsAsync<EngineInstallException>(() => installer.InstallAsync(release, choice, ct: Ct));

        Assert.Contains("does not start", ex.Message, StringComparison.Ordinal);
        Assert.Contains("libcuda", ex.Message, StringComparison.Ordinal);
        Assert.Empty(installer.List());
        Assert.Empty(Directory.GetDirectories(Engines));
    }

    [Fact]
    public async Task ArchiveEntriesCannotEscapeTheEngineFolder()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var evil = Tar("llama-b123", (w, top) =>
        {
            File_(w, "../../escaped.txt", "pwned", Plain);
            File_(w, $"{top}/llama-server", "#!/bin/sh\n", Exec);
        });
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false, mainBytes: evil);
        await using var _ = server;
        await using var __ = downloads;

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release, choice, ct: Ct));

        Assert.False(File.Exists(Path.Combine(_profile.Root, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_profile.Root)!, "escaped.txt")));
        Assert.Empty(installer.List());
    }

    [Fact]
    public async Task LinksCannotPointOutsideEither()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "tar.gz engines are Linux and macOS builds");
        var evil = Tar("llama-b123", (w, top) =>
        {
            w.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, $"{top}/passwd") { LinkName = "/etc/passwd" });
            File_(w, $"{top}/llama-server", "#!/bin/sh\n", Exec);
        });
        var (installer, release, choice, server, downloads) = await SetUpAsync(cuda: false, mainBytes: evil);
        await using var _ = server;
        await using var __ = downloads;

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(release, choice, ct: Ct));
    }
}
