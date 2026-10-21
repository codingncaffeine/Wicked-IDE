using Wicked.Core.Catalog;
using Wicked.Core.Engines;
using Wicked.Core.Hub;

namespace Wicked.Core.Models;

/// <summary>What the Settings page chooses for the model half. Applied with <see cref="ModelServices.Configure"/>.</summary>
/// <param name="HuggingFaceToken">Sent to the Hub (and only to it); null or empty for anonymous.</param>
/// <param name="BandwidthBytesPerSecond">Total download speed cap, or null for unlimited.</param>
/// <param name="StoreLocation">Where new installs go; null or empty for the default under the data folder. Models already installed stay where they are.</param>
/// <param name="Backend">The engine build the user insists on, or null to let the machine decide.</param>
/// <param name="DefaultUpdatePolicy">What a model without a policy of its own does when it has a newer upload.</param>
/// <param name="CheckForUpdates">Whether the scheduler looks for updates at all (it still does when asked with "Check now").</param>
public sealed record ModelOptions(
    string? HuggingFaceToken = null,
    long? BandwidthBytesPerSecond = null,
    string? StoreLocation = null,
    EngineBackendKind? Backend = null,
    UpdatePolicy DefaultUpdatePolicy = UpdatePolicy.Notify,
    bool CheckForUpdates = true);

/// <summary>What happens when a model has a newer upload (D7). Kept per model in its <c>wicked.json</c>; <see cref="ModelOptions.DefaultUpdatePolicy"/> covers models that have none.</summary>
public enum UpdatePolicy
{
    /// <summary>Say so and wait to be told (the default).</summary>
    Notify,

    /// <summary>Never look, never offer.</summary>
    Pin,

    /// <summary>Download the new file beside the old one, swap when verified, keep the old one until the new one has answered a prompt.</summary>
    Auto,
}

public static class UpdatePolicies
{
    /// <summary>The key in a model's <c>wicked.json</c> settings.</summary>
    public const string SettingKey = "update.policy";

    public static string Name(UpdatePolicy policy) => policy switch { UpdatePolicy.Pin => "pin", UpdatePolicy.Auto => "auto", _ => "notify" };

    public static UpdatePolicy? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "pin" => UpdatePolicy.Pin,
        "auto" => UpdatePolicy.Auto,
        "notify" => UpdatePolicy.Notify,
        _ => null,
    };
}

/// <summary>What the settings file says about the model half, in the form <see cref="ModelServices.Configure"/> takes.</summary>
public static class ModelOptionsFile
{
    public static ModelOptions From(Settings.WickedSettings s) => new(
        s.HuggingFaceToken,
        s.DownloadLimit > 0 ? (long)(s.DownloadLimit * 1_048_576) : null,
        s.StoreLocation,
        s.RuntimeBackend?.ToLowerInvariant() switch
        {
            "cuda" => EngineBackendKind.Cuda,
            "vulkan" => EngineBackendKind.Vulkan,
            "rocm" => EngineBackendKind.Rocm,
            "cpu" => EngineBackendKind.Cpu,
            _ => null,
        },
        UpdatePolicies.Parse(s.UpdatePolicy) ?? UpdatePolicy.Notify,
        s.CheckForUpdates);
}

/// <summary>Why a model is flagged on the installed list.</summary>
public enum UpdateKind
{
    /// <summary>The same repo has a newer commit than the one installed (D7, channel a).</summary>
    NewUpload,

    /// <summary>The catalog says a newer model replaces this one (D7, channel b).</summary>
    Superseded,
}

/// <summary>An installed model that has something newer. <c>Detail</c> is the line shown: what changed or what replaces it.</summary>
public sealed record ModelUpdate(string ModelId, UpdateKind Kind, string Detail, string? LatestRevision = null)
{
    /// <summary>The commit installed now.</summary>
    public string? InstalledRevision { get; init; }

    /// <summary>What the files that changed weigh now and weighed when installed, in bytes (0 when unknown).</summary>
    public long InstalledSize { get; init; }

    public long NewSize { get; init; }

    /// <summary>The first lines of the commits since the installed one, newest first.</summary>
    public IReadOnlyList<string> Titles { get; init; } = [];

    /// <summary>The files whose contents changed, with the hashes the Hub gives them. Empty when the update cannot be applied by Wicked (the repo renamed its files).</summary>
    public IReadOnlyList<UpdateFile> Files { get; init; } = [];

    /// <summary>The catalog model that replaces this one, for a superseded model.</summary>
    public string? ReplacementName { get; init; }

    public bool CanApply => Kind == UpdateKind.NewUpload && Files.Count > 0 && LatestRevision is not null;
}

/// <summary>A file of a newer upload: its path in the repo, its size and the sha256 it must have once downloaded.</summary>
public sealed record UpdateFile(string Path, long Size, string Sha256);

/// <summary>The outcome of one "Check for updates".</summary>
public sealed record UpdateCheck(int Checked, int Failed, IReadOnlyList<ModelUpdate> Found, string? Problem = null)
{
    /// <summary>Models skipped because their policy is "pin".</summary>
    public int Pinned { get; init; }

    /// <summary>How long the Hub asked to wait before the next request, when it refused for rate limiting.</summary>
    public TimeSpan? RetryAfter { get; init; }
}

/// <summary>A licence the user has to accept before the first download of a model (D8).</summary>
public sealed record LicenceRequest(string ModelName, string LicenseId, string Url);

/// <summary>Raised instead of downloading a model whose licence has not been accepted.</summary>
public sealed class LicenceNotAcceptedException(LicenceRequest request) : InvalidOperationException($"{request.ModelName} is under the {request.LicenseId} licence, which has to be accepted first")
{
    public LicenceRequest Request { get; } = request;
}

/// <summary>The install was cancelled by the user; nothing is left on disk.</summary>
public sealed class InstallCancelledException(string modelId) : OperationCanceledException($"The install of {modelId} was cancelled")
{
    public string ModelId { get; } = modelId;
}

/// <summary>A repo on the Hub seen as an installable thing: its info, its GGUF variants, and the shape the fit is judged from.</summary>
/// <param name="Model">What the Hub says about the repo.</param>
/// <param name="Variants">The quants the repo offers, smallest first.</param>
/// <param name="Shape">From the catalog when it knows the repo, else read from the GGUF header; null when neither could be had (fit unknown).</param>
/// <param name="NativeContext">The model's own maximum context, or 0 when unknown.</param>
/// <param name="ShapeSource">Where the shape came from, for a tooltip: "catalog", "GGUF header" or the reason it is missing.</param>
public sealed record HubRepoDetail(HubModel Model, IReadOnlyList<GgufVariant> Variants, ModelShape? Shape, int NativeContext, string ShapeSource);

/// <summary>The engine as the Runtime page shows it.</summary>
public sealed record RuntimeStatus(
    EngineInstall? Current,
    EngineInstall? Previous,
    EngineReport? Report,
    EngineBackendKind? Preferred,
    IReadOnlyList<EngineChoice> Choices);

/// <summary>A newer engine build for the chosen backend. <c>From</c> is the build installed now (null when none).</summary>
public sealed record EngineUpdate(EngineInstall? From, EngineRelease Release, EngineChoice Choice)
{
    public string Id => $"{Release.Tag}-{Choice.Backend.Slug}";
}
