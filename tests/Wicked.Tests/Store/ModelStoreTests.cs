using Wicked.Core.Store;

namespace Wicked.Tests;

public sealed class ModelStoreTests : IDisposable
{
    private readonly TempProfile _profile = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Root => Path.Combine(_profile.Root, "models");

    public void Dispose() => _profile.Dispose();

    private async Task Gguf(string relative, int filler = 1000, uint context = 8192) =>
        await new GgufBuilder().Llama(context: context).WriteAsync(Path.Combine(Root, relative), filler);

    [Fact]
    public async Task ScansManagedAndHandCopiedModels()
    {
        await Gguf("unsloth/Qwen3-GGUF/Qwen3-Q4_K_M.gguf", 2000, 40960);
        await Gguf("unsloth/Qwen3-GGUF/Qwen3-Q8_0.gguf", 3000);
        await Gguf("unsloth/Qwen3-GGUF/mmproj-F16.gguf", 100);
        await Gguf("MyFolder/handmade-Q5_K_M.gguf");
        await Gguf("loose-Q6_K.gguf");
        var store = new ModelStore(Root);
        await store.RegisterAsync("unsloth/Qwen3-GGUF", "unsloth/Qwen3-GGUF", "abc123",
            [("Qwen3-Q4_K_M.gguf", "sha-q4"), ("Qwen3-Q8_0.gguf", "sha-q8")], licenseId: "apache-2.0", catalogId: "qwen3", ct: Ct);

        var models = await store.ScanAsync(Ct);

        Assert.Equal(["local/MyFolder", "local/loose-Q6_K", "unsloth/Qwen3-GGUF"], models.Select(m => m.Id).Order(StringComparer.Ordinal));
        var qwen = models.Single(m => m.Id == "unsloth/Qwen3-GGUF");
        Assert.False(qwen.HandCopied);
        Assert.Equal("abc123", qwen.Manifest!.Revision);
        Assert.Equal("apache-2.0", qwen.Manifest.LicenseId);
        Assert.Equal(["unsloth/Qwen3-GGUF:Q4_K_M", "unsloth/Qwen3-GGUF:Q8_0"], qwen.Variants.Select(v => v.Id).Order(StringComparer.Ordinal));
        var q4 = qwen.Variants.Single(v => v.Quant == "Q4_K_M");
        Assert.Equal(40960, q4.Gguf!.ContextLength);
        Assert.Equal("sha-q4", q4.Files[0].Sha256);
        Assert.EndsWith("mmproj-F16.gguf", q4.MmprojPath, StringComparison.Ordinal);
        Assert.True(models.Single(m => m.Id == "local/MyFolder").HandCopied);
        Assert.Equal("local/loose-Q6_K", Assert.Single(models.Single(m => m.Id == "local/loose-Q6_K").Variants).Id);
        Assert.Equal("llama", models.Single(m => m.Id == "local/MyFolder").Variants[0].Gguf!.Architecture);
    }

    [Fact]
    public async Task ShardsAndSubfoldersBelongToTheirRepo()
    {
        await Gguf("bartowski/Big-GGUF/Q6_K/Big-Q6_K-00001-of-00002.gguf", 500);
        await Gguf("bartowski/Big-GGUF/Q6_K/Big-Q6_K-00002-of-00002.gguf", 500);
        var store = new ModelStore(Root);

        var model = Assert.Single(await store.ScanAsync(Ct));

        Assert.Equal("bartowski/Big-GGUF", model.Id);
        var variant = Assert.Single(model.Variants);
        Assert.Equal("bartowski/Big-GGUF:Q6_K", variant.Id);
        Assert.Equal(2, variant.Files.Count);
        Assert.EndsWith("00001-of-00002.gguf", variant.PrimaryPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreadableGgufStillListsWithoutHeaderInfo()
    {
        Directory.CreateDirectory(Path.Combine(Root, "a", "b"));
        await File.WriteAllTextAsync(Path.Combine(Root, "a", "b", "broken-Q4_K_M.gguf"), "garbage", Ct);
        var store = new ModelStore(Root);

        var model = Assert.Single(await store.ScanAsync(Ct));

        Assert.Null(model.Variants[0].Gguf);
    }

    [Fact]
    public async Task SettingsAndManifestRoundTrip()
    {
        await Gguf("o/n/n-Q4_K_M.gguf");
        var store = new ModelStore(Root);

        await store.SetSettingAsync("o/n", "context", "16384", Ct);
        await store.SetSettingAsync("o/n", "gpu-layers", "all", Ct);
        await store.SetSettingAsync("o/n", "gpu-layers", null, Ct);

        var manifest = await store.ReadManifestAsync("o/n", Ct);
        Assert.Equal("16384", manifest!.Settings["context"]);
        Assert.False(manifest.Settings.ContainsKey("gpu-layers"));
        Assert.False((await store.GetAsync("o/n", Ct))!.HandCopied);
        Assert.False(File.Exists(Path.Combine(Root, "o", "n", "wicked.json.tmp")));
    }

    [Fact]
    public async Task DeletesOneVariantThenTheWholeModelButNothingElse()
    {
        await Gguf("o/n/n-Q4_K_M.gguf");
        await Gguf("o/n/n-Q8_0.gguf");
        await File.WriteAllTextAsync(Path.Combine(Root, "o", "n", "README.md"), "keep me", Ct);
        await File.WriteAllTextAsync(Path.Combine(Root, "o", "n", "n-Q8_0.gguf.part"), "partial", Ct);
        var store = new ModelStore(Root);
        await store.RegisterAsync("o/n", "o/n", "r", [("n-Q4_K_M.gguf", null), ("n-Q8_0.gguf", null)], ct: Ct);

        await store.DeleteVariantAsync("o/n", "q8_0", Ct);

        Assert.False(File.Exists(Path.Combine(Root, "o", "n", "n-Q8_0.gguf")));
        Assert.False(File.Exists(Path.Combine(Root, "o", "n", "n-Q8_0.gguf.part")));
        Assert.True(File.Exists(Path.Combine(Root, "o", "n", "n-Q4_K_M.gguf")));
        Assert.Equal(["n-Q4_K_M.gguf"], (await store.ReadManifestAsync("o/n", Ct))!.Files.Select(f => f.Path));

        await store.DeleteAsync("o/n", Ct);

        Assert.False(File.Exists(Path.Combine(Root, "o", "n", "n-Q4_K_M.gguf")));
        Assert.False(File.Exists(Path.Combine(Root, "o", "n", "wicked.json")));
        Assert.Equal("keep me", await File.ReadAllTextAsync(Path.Combine(Root, "o", "n", "README.md"), Ct));
    }

    [Fact]
    public async Task DeletingTheLastFileRemovesTheEmptyFolders()
    {
        await Gguf("o/n/n-Q4_K_M.gguf");
        var store = new ModelStore(Root);

        await store.DeleteAsync("o/n", Ct);

        Assert.False(Directory.Exists(Path.Combine(Root, "o")));
        Assert.True(Directory.Exists(Root));
    }

    [Fact]
    public async Task DeletingALooseFileLeavesTheRestOfTheRoot()
    {
        await Gguf("loose-Q6_K.gguf");
        await Gguf("other-Q6_K.gguf");
        var store = new ModelStore(Root);

        await store.DeleteAsync("local/loose-Q6_K", Ct);

        Assert.False(File.Exists(Path.Combine(Root, "loose-Q6_K.gguf")));
        Assert.True(File.Exists(Path.Combine(Root, "other-Q6_K.gguf")));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../b")]
    [InlineData("/etc/passwd")]
    [InlineData("onlyone")]
    [InlineData("a/b/c")]
    [InlineData("a/.hidden/..")]
    public void RejectsIdsThatAreNotTwoPlainNames(string id) =>
        Assert.Throws<ArgumentException>(() => new ModelStore(Root).DirectoryFor(id));

    [Theory]
    [InlineData("../x.gguf")]
    [InlineData("sub/../../x.gguf")]
    [InlineData("/abs.gguf")]
    [InlineData("wicked.json")]
    public void RejectsFileNamesThatLeaveTheModelFolder(string file) =>
        Assert.Throws<ArgumentException>(() => new ModelStore(Root).PathFor("o/n", file));

    [Fact]
    public void BuildsPathsInsideTheModelFolder()
    {
        var store = new ModelStore(Root);

        Assert.Equal(Path.Combine(Path.GetFullPath(Root), "unsloth", "Qwen3", "Q4_K_M", "a.gguf"), store.PathFor("unsloth/Qwen3", "Q4_K_M/a.gguf"));
    }

    [Fact]
    public async Task VerifyFindsACorruptedFile()
    {
        await Gguf("o/n/n-Q4_K_M.gguf");
        var store = new ModelStore(Root);
        var path = store.PathFor("o/n", "n-Q4_K_M.gguf");
        var good = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path, Ct)));
        await store.RegisterAsync("o/n", "o/n", "r", [("n-Q4_K_M.gguf", good)], ct: Ct);

        Assert.Empty(await store.VerifyAsync("o/n", ct: Ct));

        await using (var f = new FileStream(path, FileMode.Open, FileAccess.Write)) { f.Seek(500, SeekOrigin.Begin); f.WriteByte(0xFF); f.WriteByte(0xEE); }
        Assert.Equal(["n-Q4_K_M.gguf"], await store.VerifyAsync("o/n", ct: Ct));
    }

    [Fact]
    public void ReportsFreeSpaceOfTheStoreDrive()
    {
        var free = new ModelStore(Root).FreeSpace();

        Assert.True(free is null or > 0);
        Assert.True(Directory.Exists(Root));
    }
}
