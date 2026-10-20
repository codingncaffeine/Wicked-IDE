using System.Globalization;
using System.Runtime.InteropServices;

namespace Wicked.Core.Engines;

public enum EngineOs
{
    Linux,
    Windows,
    MacOs,
}

public enum EngineArch
{
    X64,
    Arm64,
}

/// <summary>The operating system and CPU architecture an engine build must match.</summary>
public sealed record EnginePlatform(EngineOs Os, EngineArch Arch)
{
    public static EnginePlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? EngineOs.Windows : OperatingSystem.IsMacOS() ? EngineOs.MacOs : EngineOs.Linux,
        RuntimeInformation.OSArchitecture == Architecture.Arm64 ? EngineArch.Arm64 : EngineArch.X64);

    public string ArchName => Arch == EngineArch.Arm64 ? "arm64" : "x64";

    public string ArchiveExtension => Os == EngineOs.Windows ? ".zip" : ".tar.gz";

    public string ServerFileName => Os == EngineOs.Windows ? "llama-server.exe" : "llama-server";
}

/// <summary>One file of a llama.cpp release. <see cref="Sha256"/> is lower-case hex from GitHub's <c>digest</c> field, null if the release predates digests.</summary>
public sealed record EngineAsset(string Name, long Size, Uri Url, string? Sha256);

/// <summary>A llama.cpp release: a <c>b#####</c> tag, marked prerelease by the project, with its downloadable builds.</summary>
public sealed record EngineRelease(string Tag, int Build, DateTimeOffset Published, bool Prerelease, IReadOnlyList<EngineAsset> Assets)
{
    /// <summary>The build number of a <c>b11759</c> tag, or null for other tags (the project's <c>/releases/latest</c> is a <c>v0.x</c> tag with no assets).</summary>
    public static int? ParseBuild(string tag) =>
        tag.Length > 1 && tag[0] == 'b' && int.TryParse(tag.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    public EngineAsset? FindAsset(string suffix, string extension, bool cudart = false)
    {
        var prefix = cudart ? "cudart-llama-" : "llama-";
        var tail = "-bin-" + suffix + extension;
        return Assets.FirstOrDefault(a => a.Name.StartsWith(prefix, StringComparison.Ordinal) && a.Name.EndsWith(tail, StringComparison.Ordinal)
                                         );
    }
}

public enum EngineBackendKind
{
    Cpu,
    Vulkan,
    Cuda,
    Rocm,
    Metal,
}

/// <summary>A compute backend; <see cref="Variant"/> is the toolkit version of builds that have several (CUDA 13.4 / 12.8 / 12.4, ROCm 10.0).</summary>
public sealed record EngineBackend(EngineBackendKind Kind, string? Variant = null)
{
    /// <summary>Short stable text used in folder names and settings: <c>cpu</c>, <c>vulkan</c>, <c>cuda-13.4</c>, <c>rocm-10.0</c>, <c>metal</c>.</summary>
    public string Slug => Kind switch
    {
        EngineBackendKind.Cpu => "cpu",
        EngineBackendKind.Vulkan => "vulkan",
        EngineBackendKind.Cuda => "cuda-" + Variant,
        EngineBackendKind.Rocm => "rocm-" + Variant,
        _ => "metal",
    };

    public string DisplayName => Kind switch
    {
        EngineBackendKind.Cpu => "CPU",
        EngineBackendKind.Vulkan => "Vulkan",
        EngineBackendKind.Cuda => "CUDA " + Variant,
        EngineBackendKind.Rocm => "ROCm " + Variant,
        _ => "Metal",
    };
}

/// <summary>
/// One concrete build to install: the backend, and the asset-name tail for this platform (<c>ubuntu-cuda-13.4-x64</c>).
/// CUDA builds also need the matching runtime archive (<see cref="CudartSuffix"/>) so no system CUDA toolkit is required.
/// </summary>
public sealed record EngineChoice(EngineBackend Backend, EnginePlatform Platform, string Suffix, string? CudartSuffix, string Reason)
{
    public EngineAsset? FindMain(EngineRelease release) => release.FindAsset(Suffix, Platform.ArchiveExtension);

    public EngineAsset? FindCudart(EngineRelease release) =>
        CudartSuffix is null ? null : release.FindAsset(CudartSuffix, Platform.ArchiveExtension, cudart: true);

    /// <summary>True when the release has every archive this choice needs.</summary>
    public bool IsAvailableIn(EngineRelease release) => FindMain(release) is not null && (CudartSuffix is null || FindCudart(release) is not null);
}
