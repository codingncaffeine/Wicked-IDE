using Wicked.Core.Hardware;

namespace Wicked.Tests;

public sealed class HardwareTests
{
    private const string CpuInfo = """
        processor	: 0
        vendor_id	: AuthenticAMD
        model name	: AMD Ryzen 9 9950X3D 16-Core Processor
        flags		: fpu vme avx avx2 avx512f avx512bw sse4_2

        processor	: 1
        model name	: AMD Ryzen 9 9950X3D 16-Core Processor
        flags		: fpu vme avx avx2 avx512f avx512bw sse4_2
        """;

    private const string MemInfo = """
        MemTotal:       62914560 kB
        MemFree:         1000000 kB
        MemAvailable:   40000000 kB
        Buffers:          100000 kB
        """;

    private static string Fake(TempProfile p, string sub, string name, string content)
    {
        var path = Path.Combine(p.Root, sub, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void FakeCard(TempProfile p, string card, string vendor, string device, string? total, string? used, string slot)
    {
        var dir = $"sys/class/drm/{card}/device";
        Fake(p, dir, "vendor", vendor + "\n");
        Fake(p, dir, "device", device + "\n");
        Fake(p, dir, "uevent", $"DRIVER=x\nPCI_SLOT_NAME={slot}\n");
        if (total is not null) Fake(p, dir, "mem_info_vram_total", total + "\n");
        if (used is not null) Fake(p, dir, "mem_info_vram_used", used + "\n");
    }

    [Fact]
    public void ParsesCpuInfo()
    {
        var cpu = ProcFiles.ParseCpuInfo(CpuInfo)!;

        Assert.Equal("AMD Ryzen 9 9950X3D 16-Core Processor", cpu.Model);
        Assert.Equal(2, cpu.LogicalCores);
        Assert.True(cpu.Avx2);
        Assert.True(cpu.Avx512);
        Assert.False(ProcFiles.ParseCpuInfo("flags : avx\nprocessor : 0\n")!.Avx2);
        Assert.Null(ProcFiles.ParseCpuInfo("nothing here"));
    }

    [Fact]
    public void ParsesMemInfoInBytes()
    {
        var (total, available) = ProcFiles.ParseMemInfo(MemInfo)!.Value;

        Assert.Equal(62914560L * 1024, total);
        Assert.Equal(40000000L * 1024, available);
        Assert.Null(ProcFiles.ParseMemInfo("MemFree: 1 kB"));
        Assert.Equal((100L + 5) * 1024, ProcFiles.ParseMemInfo("MemTotal: 500 kB\nMemFree: 100 kB\nCached: 5 kB")!.Value.Available);
    }

    [Fact]
    public async Task ReadsAFakeProcAndSysTree()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "the fake tree is Linux's /proc and /sys");
        using var p = new TempProfile();
        Fake(p, "proc", "cpuinfo", CpuInfo);
        Fake(p, "proc", "meminfo", MemInfo);
        FakeCard(p, "card0", "0x1002", "0x744c", "25753026560", "1073741824", "0000:03:00.0");
        FakeCard(p, "card1", "0x8086", "0x4680", null, null, "0000:00:02.0");
        Directory.CreateDirectory(Path.Combine(p.Root, "sys/class/drm/card0-DP-1"));
        var ids = Fake(p, "etc", "pci.ids", "1002  Advanced Micro Devices, Inc. [AMD/ATI]\n\t744c  Navi 31 [Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M]\n\t\t1002 0e3a  Sub\n8086  Intel Corporation\n\t4680  AlderLake-S GT1 [UHD Graphics 770]\n");

        var info = await HardwareProbe.DetectAsync(
            new HardwareSources(Path.Combine(p.Root, "proc"), Path.Combine(p.Root, "sys"), UseNvml: false, PciIdsPath: ids), TestContext.Current.CancellationToken);

        Assert.Equal(2, info.Cpu.LogicalCores);
        Assert.Equal(62914560L * 1024, info.RamTotal);
        Assert.Equal(2, info.Gpus.Count);
        var amd = info.Gpus.Single(g => g.Vendor == GpuVendor.Amd);
        Assert.Equal("AMD Radeon RX 7900 XT/7900 XTX/7900 GRE/7900M", amd.Name);
        Assert.Equal(25753026560, amd.VramTotal);
        Assert.Equal(25753026560 - 1073741824, amd.VramFree);
        Assert.Equal("1002:744c", amd.PciId);
        var intel = info.Gpus.Single(g => g.Vendor == GpuVendor.Intel);
        Assert.Equal("Intel UHD Graphics 770", intel.Name);
        Assert.Null(intel.VramTotal);
        Assert.Same(amd, info.PrimaryGpu);
    }

    [Fact]
    public async Task MissingSourcesAreNotErrors()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads Linux paths");
        using var p = new TempProfile();

        var info = await HardwareProbe.DetectAsync(
            new HardwareSources(Path.Combine(p.Root, "nope"), Path.Combine(p.Root, "nada"), UseNvml: false), TestContext.Current.CancellationToken);

        Assert.Empty(info.Gpus);
        Assert.Null(info.PrimaryGpu);
        Assert.Equal(0, info.RamTotal);
        Assert.True(info.Cpu.LogicalCores > 0);
    }

    [Fact]
    public void AMachineWithoutNvmlHasNoNvidiaGpus() =>
        Assert.Empty(Nvml.Query(["libdefinitely-not-nvml.so.9"]));

    [Fact]
    public async Task TheRealMachineIsDescribedWithoutThrowing()
    {
        var info = await HardwareProbe.DetectAsync(TestContext.Current.CancellationToken);

        Assert.True(info.Cpu.LogicalCores > 0);
        Assert.NotEmpty(info.Cpu.Model);
    }
}
