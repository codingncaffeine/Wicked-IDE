using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Http;
using Wicked.Core.Models;

namespace Wicked.Tests.Models;

/// <summary>
/// A whole little world for window tests: a signed catalog of small models, the Hub that serves their files, and GitHub with an
/// engine. The model half runs against it through the real classes, so a window test walks the user's path with no network.
/// Three models are the recommendation (Chat for everyday, Coder for coding, Quick for fast, under a community licence), and
/// the rest only show up on the Browse page.
/// </summary>
internal sealed class ModelWorld : IAsyncDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private ModelWorld(HubStandIn hub, ModelCatalog catalog)
    {
        Hub = hub;
        Catalog = catalog;
    }

    public HubStandIn Hub { get; }

    public ModelCatalog Catalog { get; }

    public const string Chat = "test/Tiny-Chat-GGUF";
    public const string Coder = "test/Tiny-Coder-GGUF";
    public const string Quick = "test/Tiny-Quick-GGUF";
    public const string Gated = "test/Tiny-Gated-GGUF";
    public const string Old = "test/Tiny-Chat-1-GGUF";

    /// <summary>The bytes of a model's file in this world.</summary>
    public static byte[] Weights(string repo, string quant) => StandIns.Weights(Size(repo, quant), (byte)(repo.Length + quant.Length));

    private static int Size(string repo, string quant) => (repo, quant) switch
    {
        (Chat, "Q8_0") => 3_500_000,
        (Chat, _) => 2_000_000,
        (Coder, "Q8_0") => 1_600_000,
        (Coder, _) => 1_000_000,
        (Quick, "Q8_0") => 900_000,
        (Quick, _) => 600_000,
        (_, "Q8_0") => 700_000,
        _ => 400_000,
    };

    private static string FileName(string repo, string quant) => $"{repo[(repo.IndexOf('/') + 1)..].Replace("-GGUF", "", StringComparison.Ordinal)}-{quant}.gguf"; // not a path: a repo id

    private static QuantTier Tier(string repo, string quant, int quality)
    {
        var bytes = Weights(repo, quant);
        return new QuantTier(quant, [new CatalogFile(FileName(repo, quant), bytes.Length, StandIns.Sha(bytes))], quality);
    }

    private static readonly ModelShape Shape = new(8, 8, 0, 0, 2, 64, 64, 64, 64);

    private static CatalogModel Model(string id, string name, string repo, string license, string? licenseUrl, bool gated, DateOnly released, RoleScores scores, params string[] supersedes) =>
        new(id, name, repo, "rev1", license, licenseUrl, gated, [Capability.Chat, Capability.Tools], 32768, new ParameterCount(3), released, scores, supersedes, Shape,
            [Tier(repo, "Q4_K_M", 90), Tier(repo, "Q8_0", 99)]);

    public static async Task<ModelWorld> StartAsync()
    {
        var models = new[]
        {
            Model("tiny-chat", "Tiny Chat", Chat, "apache-2.0", null, false, new(2026, 9, 1), new(Everyday: 90, Coding: 70, Fast: 50), "tiny-chat-1"),
            Model("tiny-chat-1", "Tiny Chat 1", Old, "apache-2.0", null, false, new(2026, 3, 1), new(Everyday: 55)),
            Model("tiny-coder", "Tiny Coder", Coder, "mit", null, false, new(2026, 8, 1), new(Everyday: 60, Coding: 95)),
            Model("tiny-quick", "Tiny Quick", Quick, "gemma", "https://example.test/gemma-terms", false, new(2026, 7, 1), new(Everyday: 40, Fast: 90)),
            Model("tiny-gated", "Tiny Gated", Gated, "mit", null, true, new(2026, 6, 1), new(Fast: 20)),
        };
        var catalog = new ModelCatalog(1, DateTimeOffset.UtcNow.AddYears(1),
        [
            new CatalogFamily("tiny", "Tiny", "Tests", "Small models for tests", [models[0], models[3], models[2], models[1]]),
            new CatalogFamily("gate", "Gated", "Tests", null, [models[4]]),
        ]);

        var repos = models.Select(m => new HubRepo(m.Repo, "rev1") { License = m.LicenseId, Gated = m.Gated ? "manual" : null }).ToList();
        foreach (var (model, repo) in models.Zip(repos))
        {
            foreach (var tier in model.Tiers) repo.Files[tier.Files[0].Path] = Weights(model.Repo, tier.Quant);
        }

        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var json = Encoding.UTF8.GetBytes(catalog.ToJson());
        var signature = Convert.ToBase64String(key.SignData(json, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var hub = await HubStandIn.StartAsync(app =>
        {
            app.MapGet("/catalog.json", () => Results.Bytes(json, "application/json"));
            app.MapGet("/catalog.json.sig", () => Results.Text(signature + "\n"));
            StandIns.MapGitHub(app, 999);
        }, [.. repos]);
        var world = new ModelWorld(hub, catalog);
        world.PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        key.Dispose();
        return world;
    }

    public string PublicKey { get; private set; } = "";

    /// <summary>Services wired to this world, for a window that builds its own (see <see cref="Use"/>).</summary>
    public ModelServices Services(WickedPaths paths) =>
        new(paths, WickedHttp.CreateClient(offline: () => false), hubBase: Hub.Server.Url("/"), catalogBase: Hub.Server.Url("/"), githubBase: Hub.Server.Url("/"), catalogKey: PublicKey);

    /// <summary>Makes windows built while the returned scope is open use this world. UI tests run one at a time, so the hook is not contended.</summary>
    public IDisposable Use()
    {
        ModelsViewModel.ServicesFactory = Services;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose() => ModelsViewModel.ServicesFactory = null;
    }

    public string Store(WickedPaths paths) => Path.Combine(paths.Data, "models");

    public async ValueTask DisposeAsync()
    {
        _key.Dispose();
        await Hub.DisposeAsync();
    }
}

/// <summary>Finding things in the window the way a person looks: by what they say and do.</summary>
internal static class ModelsUi
{
    public static IEnumerable<Border> Cards(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("pick") && b.IsEffectivelyVisible);

    public static Border Card(Window window, string name) =>
        Cards(window).Single(c => ((PickCard)c.DataContext!).Name == name);

    public static Button? Named(Control root, string automationName) =>
        root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => Avalonia.Automation.AutomationProperties.GetName(b) == automationName && b.IsEffectivelyVisible);

    public static Button? Labelled(Control root, string content) =>
        root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == content && b.IsEffectivelyVisible);

    public static void Shot(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        Ui.Pump(0.3);
        using var frame = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
        frame?.Save(Path.Combine(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
}

internal static class VisualText
{
    /// <summary>All the text a control shows, for "it says Installed" checks.</summary>
    public static string GetVisualDescendantsText(this Control root) =>
        string.Join("\n", root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text));
}
