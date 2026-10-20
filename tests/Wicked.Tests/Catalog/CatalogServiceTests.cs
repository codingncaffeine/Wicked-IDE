using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Catalog;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class CatalogServiceTests : IDisposable
{
    private readonly TempProfile _profile = new();
    private readonly HttpClient _http = WickedHttp.CreateClient(offline: () => false);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _http.Dispose();
        _key.Dispose();
        _profile.Dispose();
    }

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private string CacheDir => Path.Combine(_profile.Root, "cache");

    private (byte[] Json, string Signature) Signed(DateTimeOffset generated, string name = "online", int schema = 1)
    {
        var catalog = new ModelCatalog(schema, generated, [new CatalogFamily("f", name, "p", null, [])]);
        var json = Encoding.UTF8.GetBytes(catalog.ToJson());
        return (json, Convert.ToBase64String(_key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    }

    private static Task<LocalServer> Serve((byte[] Json, string Signature) file, Action<WebApplication>? more = null) =>
        LocalServer.StartAsync(app =>
        {
            app.MapGet("/catalog.json", () => Results.Bytes(file.Json, "application/json"));
            app.MapGet("/catalog.json.sig", () => Results.Text(file.Signature + "\n"));
            more?.Invoke(app);
        });

    private CatalogService Service(LocalServer server) => new(_http, CacheDir, server.BaseAddress, PublicKey);

    [Fact]
    public async Task TheBuiltInCopyIsUsedOffline()
    {
        var load = await new CatalogService(_http, CacheDir).LoadAsync(allowNetwork: false, ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
        Assert.NotEmpty(load.Catalog.Families);
    }

    [Fact]
    public async Task ANewerSignedCatalogIsFetchedVerifiedAndCached()
    {
        await using var server = await Serve(Signed(DateTimeOffset.UtcNow.AddDays(30)));
        var service = Service(server);

        var online = await service.LoadAsync(ct: Ct);
        var offline = await service.LoadAsync(allowNetwork: false, ct: Ct);

        Assert.Equal(CatalogOrigin.Network, online.Origin);
        Assert.Equal("online", online.Catalog.Families[0].Name);
        Assert.Equal(CatalogOrigin.Cache, offline.Origin);
        Assert.Equal("online", offline.Catalog.Families[0].Name);
        Assert.True(File.Exists(Path.Combine(CacheDir, "catalog.json.sig")));
    }

    [Fact]
    public async Task AnUnsignedOrTamperedCatalogIsRefused()
    {
        var good = Signed(DateTimeOffset.UtcNow.AddDays(30));
        var tampered = (Json: Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(good.Json).Replace("online", "evil!!", StringComparison.Ordinal)), good.Signature);
        await using var server = await Serve(tampered);

        var load = await Service(server).LoadAsync(ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
        Assert.NotNull(load.Note);
        Assert.False(File.Exists(Path.Combine(CacheDir, "catalog.json")));
    }

    [Fact]
    public async Task ACatalogSignedByAnotherKeyIsRefused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var catalog = new ModelCatalog(1, DateTimeOffset.UtcNow.AddDays(30), []);
        var json = Encoding.UTF8.GetBytes(catalog.ToJson());
        var signature = Convert.ToBase64String(other.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        await using var server = await Serve((json, signature));

        Assert.Equal(CatalogOrigin.Embedded, (await Service(server).LoadAsync(ct: Ct)).Origin);
    }

    [Fact]
    public async Task AnOlderSignedCatalogCannotReplaceANewerOne()
    {
        await using var server = await Serve(Signed(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));

        var load = await Service(server).LoadAsync(ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
        Assert.DoesNotContain(load.Catalog.Families, f => f.Name == "online");
    }

    [Fact]
    public async Task ACatalogWithAFutureSchemaIsIgnored()
    {
        await using var server = await Serve(Signed(DateTimeOffset.UtcNow.AddDays(30), schema: 99));

        Assert.Equal(CatalogOrigin.Embedded, (await Service(server).LoadAsync(ct: Ct)).Origin);
    }

    [Fact]
    public async Task ATamperedCacheFileIsIgnored()
    {
        await using var server = await Serve(Signed(DateTimeOffset.UtcNow.AddDays(30)));
        await Service(server).LoadAsync(ct: Ct);
        await File.AppendAllTextAsync(Path.Combine(CacheDir, "catalog.json"), " ", Ct);

        var load = await Service(server).LoadAsync(allowNetwork: false, ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
    }

    [Fact]
    public async Task NetworkFailureFallsBackWithANote()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/catalog.json", () => Results.StatusCode(500)));

        var load = await Service(server).LoadAsync(ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
        Assert.NotNull(load.Note);
    }

    [Fact]
    public async Task OfflineModeNeverTouchesTheInternet()
    {
        using var offline = WickedHttp.CreateClient(offline: () => true);

        var load = await new CatalogService(offline, CacheDir).LoadAsync(ct: Ct);

        Assert.Equal(CatalogOrigin.Embedded, load.Origin);
    }
}
