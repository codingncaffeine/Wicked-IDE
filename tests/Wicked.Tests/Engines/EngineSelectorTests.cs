using Wicked.Core.Engines;
using Wicked.Core.Hardware;

namespace Wicked.Tests;

public sealed class EngineSelectorTests
{
    private static readonly EnginePlatform Linux = new(EngineOs.Linux, EngineArch.X64);
    private static readonly EnginePlatform Windows = new(EngineOs.Windows, EngineArch.X64);

    private static HardwareInfo Machine(params GpuInfo[] gpus) =>
        new(new CpuInfo("Test CPU", 16, true, false), 32L << 30, 16L << 30, gpus);

    private static GpuInfo Nvidia(string cuda) => new(GpuVendor.Nvidia, "NVIDIA GeForce", 16L << 30, 14L << 30, "615.78", Version.Parse(cuda), "10de:2c02");

    private static GpuInfo Amd() => new(GpuVendor.Amd, "AMD Radeon", 16L << 30, 16L << 30, null, null, "1002:744c");

    private static GpuInfo Intel() => new(GpuVendor.Intel, "Intel Arc", 8L << 30, null, null, null, "8086:56a0");

    [Theory]
    [InlineData("13.4", "ubuntu-cuda-13.4-x64", "ubuntu-cuda-13.4-x64")]
    [InlineData("13.0", "ubuntu-cuda-13.4-x64", "ubuntu-cuda-13.4-x64")]
    [InlineData("12.9", "ubuntu-cuda-12.8-x64", "ubuntu-cuda-12.8-x64")]
    [InlineData("12.8", "ubuntu-cuda-12.8-x64", "ubuntu-cuda-12.8-x64")]
    [InlineData("12.6", "ubuntu-vulkan-x64", null)]
    [InlineData("11.8", "ubuntu-vulkan-x64", null)]
    public void LinuxNvidiaFollowsTheDriversCudaLevel(string cuda, string expectedMain, string? expectedCudart)
    {
        var choice = EngineSelector.Choose(Machine(Nvidia(cuda)), Linux);

        Assert.Equal(expectedMain, choice.Suffix);
        Assert.Equal(expectedCudart, choice.CudartSuffix);
    }

    [Theory]
    [InlineData("13.4", "win-cuda-13.4-x64")]
    [InlineData("12.9", "win-cuda-12.4-x64")]
    [InlineData("12.4", "win-cuda-12.4-x64")]
    [InlineData("12.2", "win-vulkan-x64")]
    public void WindowsNvidiaPicksTheMatchingBuild(string cuda, string expected) =>
        Assert.Equal(expected, EngineSelector.Choose(Machine(Nvidia(cuda)), Windows).Suffix);

    [Fact]
    public void AmdAndIntelDefaultToVulkan()
    {
        Assert.Equal("ubuntu-vulkan-x64", EngineSelector.Choose(Machine(Amd()), Linux).Suffix);
        Assert.Equal("ubuntu-vulkan-x64", EngineSelector.Choose(Machine(Intel()), Linux).Suffix);
        Assert.Equal("win-vulkan-x64", EngineSelector.Choose(Machine(Amd()), Windows).Suffix);
    }

    [Fact]
    public void AmdGetsRocmOnlyWhenChosen()
    {
        var choice = EngineSelector.Choose(Machine(Amd()), Linux, EngineBackendKind.Rocm);

        Assert.Equal("ubuntu-rocm-10.0-x64", choice.Suffix);
        Assert.Equal("rocm-10.0", choice.Backend.Slug);
        Assert.Equal("ubuntu-vulkan-x64", EngineSelector.Candidates(Machine(Amd()), Linux, EngineBackendKind.Rocm)[1].Suffix);
    }

    [Fact]
    public void NoGpuMeansTheCpuBuild()
    {
        Assert.Equal("ubuntu-x64", EngineSelector.Choose(Machine(), Linux).Suffix);
        Assert.Equal("win-cpu-x64", EngineSelector.Choose(Machine(), Windows).Suffix);
        Assert.Equal("ubuntu-arm64", EngineSelector.Choose(Machine(), new(EngineOs.Linux, EngineArch.Arm64)).Suffix);
    }

    [Fact]
    public void MacUsesMetalOnAppleSilicon()
    {
        var choice = EngineSelector.Choose(Machine(), new(EngineOs.MacOs, EngineArch.Arm64));

        Assert.Equal("macos-arm64", choice.Suffix);
        Assert.Equal(EngineBackendKind.Metal, choice.Backend.Kind);
        Assert.Equal(".tar.gz", choice.Platform.ArchiveExtension);
    }

    [Fact]
    public void TheUserCanForceCpuOrVulkanOnANvidiaBox()
    {
        var hw = Machine(Nvidia("13.4"));

        Assert.Equal("ubuntu-x64", EngineSelector.Choose(hw, Linux, EngineBackendKind.Cpu).Suffix);
        Assert.Equal("ubuntu-vulkan-x64", EngineSelector.Choose(hw, Linux, EngineBackendKind.Vulkan).Suffix);
        Assert.Equal(["ubuntu-cuda-13.4-x64", "ubuntu-vulkan-x64", "ubuntu-x64"], EngineSelector.Candidates(hw, Linux).Select(c => c.Suffix));
    }

    [Fact]
    public void AnIntegratedAmdNextToANvidiaCardStillGetsCuda() =>
        Assert.Equal("ubuntu-cuda-13.4-x64", EngineSelector.Choose(Machine(Nvidia("13.4"), Amd()), Linux).Suffix);

    [Fact]
    public void PicksTheNewestReleaseThatHasTheBuild()
    {
        EngineAsset Asset(string name) => new(name, 1, new Uri("https://example.test/" + name), null);
        var newest = new EngineRelease("b12", 12, DateTimeOffset.UtcNow, true, [Asset("llama-b12-bin-ubuntu-x64.tar.gz")]);
        var older = new EngineRelease("b11", 11, DateTimeOffset.UtcNow, true,
            [Asset("llama-b11-bin-ubuntu-cuda-13.4-x64.tar.gz"), Asset("cudart-llama-b11-bin-ubuntu-cuda-13.4-x64.tar.gz")]);
        var cuda = EngineSelector.Choose(Machine(Nvidia("13.4")), Linux);

        Assert.Same(older, EngineReleases.Newest([newest, older], cuda));
        Assert.Null(EngineReleases.Newest([newest], cuda));
    }

    [Fact]
    public void WindowsCudartHasNoTagInItsName()
    {
        EngineAsset Asset(string name) => new(name, 1, new Uri("https://example.test/" + name), null);
        var release = new EngineRelease("b12", 12, DateTimeOffset.UtcNow, true,
            [Asset("llama-b12-bin-win-cuda-13.4-x64.zip"), Asset("cudart-llama-bin-win-cuda-13.4-x64.zip")]);
        var choice = EngineSelector.Choose(Machine(Nvidia("13.4")), Windows);

        Assert.True(choice.IsAvailableIn(release));
        Assert.Equal("cudart-llama-bin-win-cuda-13.4-x64.zip", choice.FindCudart(release)!.Name);
    }
}
