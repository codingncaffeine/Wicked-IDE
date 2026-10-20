using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Wicked.Core.Catalog;

namespace Wicked.Tests;

public sealed class CatalogSigningTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (string PublicKey, Func<byte[], string> Sign) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        return (publicKey, data => Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    [Fact]
    public void AGoodSignatureVerifiesAndAnyChangeBreaksIt()
    {
        var (publicKey, sign) = NewKey();
        var data = Encoding.UTF8.GetBytes("{\"schema\":1}\n");
        var signature = sign(data);

        Assert.True(CatalogVerifier.Verify(data, signature, publicKey));
        Assert.True(CatalogVerifier.Verify(data, signature + "\n", publicKey));
        Assert.False(CatalogVerifier.Verify(Encoding.UTF8.GetBytes("{\"schema\":2}\n"), signature, publicKey));
        Assert.False(CatalogVerifier.Verify(data, sign(Encoding.UTF8.GetBytes("other")), publicKey));
        Assert.False(CatalogVerifier.Verify(data, signature, NewKey().PublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void GarbageSignaturesAreRejectedNotThrown(string signature) =>
        Assert.False(CatalogVerifier.Verify("x"u8, signature, NewKey().PublicKey));

    [Fact]
    public void AGarbagePublicKeyIsRejectedNotThrown() =>
        Assert.False(CatalogVerifier.Verify("x"u8, Convert.ToBase64String(new byte[64]), "bm90IGEga2V5"));

    [Fact]
    public void TheBuiltInCatalogIsSignedByTheCompiledInKey()
    {
        var (json, signature) = CatalogService.ReadEmbedded();

        Assert.True(CatalogVerifier.Verify(json, signature));
        Assert.False(CatalogVerifier.Verify(json.Append((byte)' ').ToArray(), signature));
    }

    /// <summary>
    /// The shell tools against .NET: make a key with new-key.sh, sign many files with sign.sh (so the DER-to-P1363 conversion meets
    /// short and long r/s values), and verify each signature with the BCL.
    /// </summary>
    [Fact]
    public async Task SignShProducesSignaturesTheVerifierAccepts()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !Has("openssl") || !Has("bash"), "needs bash and openssl");
        var root = RepoRoot();
        using var dir = new TempProfile();
        var keyPath = Path.Combine(dir.Root, "keys", "k.pem");

        var made = await Run("bash", [Path.Combine(root, "tools", "catalog", "new-key.sh"), keyPath]);
        var publicKey = made.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("MFkw", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));

        for (var i = 0; i < 24; i++)
        {
            var file = Path.Combine(dir.Root, $"catalog{i}.json");
            await File.WriteAllTextAsync(file, $"{{\"n\":{i},\"salt\":\"{Guid.NewGuid()}\"}}\n", Ct);
            await Run("bash", [Path.Combine(root, "tools", "catalog", "sign.sh"), file, keyPath]);

            var signature = await File.ReadAllTextAsync(file + ".sig", Ct);
            Assert.Equal(64, Convert.FromBase64String(signature.Trim()).Length);
            Assert.True(CatalogVerifier.Verify(await File.ReadAllBytesAsync(file, Ct), signature, publicKey), $"signature {i} did not verify");
        }

        // A second new-key.sh refuses to replace the key.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run("bash", [Path.Combine(root, "tools", "catalog", "new-key.sh"), keyPath]));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("repository root not found");
    }

    private static bool Has(string tool) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(p => File.Exists(Path.Combine(p, tool)));

    private static async Task<string> Run(string file, string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        if (process.ExitCode != 0) throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited {process.ExitCode}: {await error}");
        return await output;
    }
}
