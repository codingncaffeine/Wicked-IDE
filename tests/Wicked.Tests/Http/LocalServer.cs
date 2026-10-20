using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Wicked.Tests;

/// <summary>A throwaway Kestrel server on 127.0.0.1 and a free port, so tests exercise real sockets without leaving the machine.</summary>
internal sealed class LocalServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalServer(WebApplication app, Uri address)
    {
        _app = app;
        BaseAddress = address;
    }

    public Uri BaseAddress { get; }

    public static async Task<LocalServer> StartAsync(Action<WebApplication> map, string host = "127.0.0.1")
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://{host}:0");
        var app = builder.Build();
        map(app);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalServer(app, new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal)));
    }

    public Uri Url(string path) => new(BaseAddress, path);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
