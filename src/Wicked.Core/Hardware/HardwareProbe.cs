using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace Wicked.Core.Hardware;

/// <summary>Where the probe reads from; the defaults are the real machine, a test points the roots at a fake tree.</summary>
/// <param name="ProcRoot">Stands in for <c>/proc</c>.</param>
/// <param name="SysRoot">Stands in for <c>/sys</c>.</param>
/// <param name="UseNvml">False skips the NVIDIA management library (tests, and machines where loading it would hang).</param>
/// <param name="PciIdsPath">A <c>pci.ids</c> file for GPU names; null searches the usual system locations.</param>
public sealed record HardwareSources(string ProcRoot = "/proc", string SysRoot = "/sys", bool UseNvml = true, string? PciIdsPath = null);

/// <summary>
/// Looks at the machine once and describes it. Every source is optional: a missing file, a missing driver or a failing
/// command is logged and skipped, never thrown, because a recommendation from half the facts beats no window at all.
/// The probe does blocking file and native reads, so it runs on the thread pool.
/// </summary>
public static class HardwareProbe
{
    public static Task<HardwareInfo> DetectAsync(CancellationToken ct = default) => DetectAsync(new HardwareSources(), ct);

    public static Task<HardwareInfo> DetectAsync(HardwareSources sources, CancellationToken ct = default) =>
        Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var (cpu, ramTotal, ramAvailable, gpus) = OperatingSystem.IsMacOS()
                ? await DetectMacAsync(ct).ConfigureAwait(false)
                : OperatingSystem.IsWindows() ? DetectWindows(sources) : DetectLinux(sources);
            return new HardwareInfo(cpu, ramTotal, ramAvailable, gpus);
        }, ct);

    private static (CpuInfo, long, long, IReadOnlyList<GpuInfo>) DetectLinux(HardwareSources s)
    {
        var cpu = Read(Path.Combine(s.ProcRoot, "cpuinfo"), ProcFiles.ParseCpuInfo) ?? FallbackCpu();
        var (total, available) = Read(Path.Combine(s.ProcRoot, "meminfo"), ProcFiles.ParseMemInfo) ?? (0, 0);
        return (cpu, total, available, MergeGpus(DrmScanner.Scan(s.SysRoot, s.PciIdsPath), s.UseNvml));
    }

    private static (CpuInfo, long, long, IReadOnlyList<GpuInfo>) DetectWindows(HardwareSources s)
    {
        long total = 0;
        long available = 0;
        try
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                total = (long)status.TotalPhys;
                available = (long)status.AvailPhys;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"GlobalMemoryStatusEx failed: {ex.Message}");
        }

        // Only NVML sees GPUs on Windows here; AMD and Intel are found by the engine's own --list-devices (D9).
        return (FallbackCpu(), total, available, MergeGpus([], s.UseNvml));
    }

    private static async Task<(CpuInfo, long, long, IReadOnlyList<GpuInfo>)> DetectMacAsync(CancellationToken ct)
    {
        async Task<string?> Sysctl(string key)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("sysctl", ["-n", key]) { RedirectStandardOutput = true, UseShellExecute = false });
                if (process is null) return null;
                var text = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return text.Trim();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Log.Warn($"sysctl {key} failed: {ex.Message}");
                return null;
            }
        }

        var brand = await Sysctl("machdep.cpu.brand_string").ConfigureAwait(false) ?? "Apple Silicon";
        var memory = long.TryParse(await Sysctl("hw.memsize").ConfigureAwait(false), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        var cpu = FallbackCpu() with { Model = brand };
        // Unified memory: Metal may use about three quarters of it, which is what llama.cpp's recommendedMaxWorkingSetSize reports.
        var gpu = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? new GpuInfo(GpuVendor.Apple, brand, memory * 3 / 4, null, null, null, null)
            : null;
        return (cpu, memory, memory / 2, gpu is null ? [] : [gpu]);
    }

    private static CpuInfo FallbackCpu() =>
        new(RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount, Avx2.IsSupported, Avx512F.IsSupported);

    /// <summary>NVML's answer replaces the DRM entries for NVIDIA cards (it knows names, free memory and the CUDA level); other vendors stay as the DRM scan found them.</summary>
    private static IReadOnlyList<GpuInfo> MergeGpus(IReadOnlyList<GpuInfo> drm, bool useNvml)
    {
        IReadOnlyList<GpuInfo> nvidia = [];
        if (useNvml)
        {
            try
            {
                nvidia = Nvml.Query();
            }
            catch (Exception ex)
            {
                Log.Warn($"NVML probe failed: {ex.Message}");
            }
        }

        return nvidia.Count == 0 ? drm : [.. nvidia, .. drm.Where(g => g.Vendor != GpuVendor.Nvidia)];
    }

    private static T? Read<T>(string path, Func<string, T?> parse)
    {
        try
        {
            return parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read {path}: {ex.Message}");
            return default;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
