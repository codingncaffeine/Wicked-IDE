using Wicked.Core.Hardware;

namespace Wicked.Core.Engines;

/// <summary>
/// Picks the llama.cpp build for a machine (D4). A pure, table-driven function of the hardware, the platform and an
/// optional user override, so it is unit-tested row by row. The first candidate is the choice; the rest are what to try
/// when a release lacks it (a release can ship without some builds).
/// </summary>
public static class EngineSelector
{
    private static readonly Version Cuda13 = new(13, 0);
    private static readonly Version Cuda128 = new(12, 8);
    private static readonly Version Cuda124 = new(12, 4);

    public static EngineChoice Choose(HardwareInfo hardware, EnginePlatform platform, EngineBackendKind? preferred = null) =>
        Candidates(hardware, platform, preferred)[0];

    public static IReadOnlyList<EngineChoice> Candidates(HardwareInfo hardware, EnginePlatform platform, EngineBackendKind? preferred = null)
    {
        var list = new List<EngineChoice>();
        void Add(EngineChoice? choice)
        {
            if (choice is not null && list.All(c => c.Suffix != choice.Suffix)) list.Add(choice);
        }

        var cuda = hardware.Gpus.Where(g => g.Vendor == GpuVendor.Nvidia).Select(g => g.CudaDriverVersion).Where(v => v is not null).Max();
        var hasNvidia = hardware.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia);
        var hasGpu = hardware.Gpus.Count > 0;

        if (platform.Os == EngineOs.MacOs)
        {
            Add(platform.Arch == EngineArch.Arm64
                ? Make(platform, new(EngineBackendKind.Metal), "macos-arm64", null, "Apple Silicon runs on Metal")
                : Make(platform, new(EngineBackendKind.Cpu), "macos-x64", null, "Intel Mac: CPU build"));
            return list;
        }

        switch (preferred)
        {
            case EngineBackendKind.Cpu:
                Add(Cpu(platform, "CPU build chosen in settings"));
                break;
            case EngineBackendKind.Vulkan:
                Add(Vulkan(platform, "Vulkan build chosen in settings"));
                break;
            case EngineBackendKind.Rocm:
                Add(Rocm(platform, "ROCm build chosen in settings"));
                break;
            case EngineBackendKind.Cuda:
                Add(CudaFor(platform, cuda ?? Cuda13, "CUDA build chosen in settings"));
                break;
        }

        if (hasNvidia && cuda is not null)
        {
            Add(CudaFor(platform, cuda, $"NVIDIA driver supports CUDA {cuda.ToString(2)}"));
        }

        if (hasGpu) Add(Vulkan(platform, hasNvidia ? "Vulkan fallback for the NVIDIA GPU" : "Vulkan works on AMD, Intel and other GPUs"));
        Add(Cpu(platform, hasGpu ? "CPU fallback" : "No supported GPU found: CPU build"));
        return list;
    }

    private static EngineChoice? CudaFor(EnginePlatform p, Version driverCuda, string reason)
    {
        // The 13.4 builds run on any CUDA 13 driver and the 12.x builds on any newer 12 driver (minor-version compatibility).
        if (driverCuda >= Cuda13) return Cuda(p, "13.4", reason);
        if (p.Arch == EngineArch.Arm64) return null;
        if (p.Os == EngineOs.Linux && driverCuda >= Cuda128) return Cuda(p, "12.8", reason);
        if (p.Os == EngineOs.Windows && driverCuda >= Cuda124) return Cuda(p, "12.4", reason);
        return null;
    }

    private static EngineChoice Cuda(EnginePlatform p, string version, string reason)
    {
        var tail = $"{(p.Os == EngineOs.Windows ? "win" : "ubuntu")}-cuda-{version}-{p.ArchName}";
        return Make(p, new(EngineBackendKind.Cuda, version), tail, tail, reason);
    }

    private static EngineChoice Vulkan(EnginePlatform p, string reason) =>
        Make(p, new(EngineBackendKind.Vulkan), $"{(p.Os == EngineOs.Windows ? "win" : "ubuntu")}-vulkan-{p.ArchName}", null, reason);

    private static EngineChoice? Rocm(EnginePlatform p, string reason) =>
        p.Arch == EngineArch.X64 ? Make(p, new(EngineBackendKind.Rocm, "10.0"), $"{(p.Os == EngineOs.Windows ? "win" : "ubuntu")}-rocm-10.0-x64", null, reason) : null;

    private static EngineChoice Cpu(EnginePlatform p, string reason) =>
        Make(p, new(EngineBackendKind.Cpu), p.Os == EngineOs.Windows ? $"win-cpu-{p.ArchName}" : $"ubuntu-{p.ArchName}", null, reason);

    private static EngineChoice Make(EnginePlatform p, EngineBackend backend, string suffix, string? cudart, string reason) =>
        new(backend, p, suffix, cudart, reason);
}
