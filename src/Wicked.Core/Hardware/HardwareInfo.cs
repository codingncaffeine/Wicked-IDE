namespace Wicked.Core.Hardware;

public enum GpuVendor
{
    Other,
    Nvidia,
    Amd,
    Intel,
    Apple,
}

/// <param name="Model">The marketing name from the CPU itself, e.g. "AMD Ryzen 9 9950X3D 16-Core Processor".</param>
/// <param name="LogicalCores">Threads the scheduler sees.</param>
/// <param name="Avx2">Whether the CPU can run the AVX2 kernels the CPU engine builds rely on.</param>
/// <param name="Avx512">AVX-512 foundation, which only some engine builds use.</param>
public sealed record CpuInfo(string Model, int LogicalCores, bool Avx2, bool Avx512);

/// <summary>
/// One graphics device. Memory figures are bytes and null when the source cannot tell (integrated GPUs share system RAM).
/// <c>CudaDriverVersion</c> is the newest CUDA the NVIDIA driver supports (13.4 for driver 615), null for other vendors;
/// <c>PciId</c> is lower-case <c>vendor:device</c>, e.g. <c>10de:2c02</c>.
/// </summary>
public sealed record GpuInfo(
    GpuVendor Vendor,
    string Name,
    long? VramTotal,
    long? VramFree,
    string? DriverVersion,
    Version? CudaDriverVersion,
    string? PciId);

/// <summary>What this machine can run models on. Immutable, so it can be handed to any thread.</summary>
public sealed record HardwareInfo(CpuInfo Cpu, long RamTotal, long RamAvailable, IReadOnlyList<GpuInfo> Gpus)
{
    /// <summary>The GPU with the most VRAM, or null when there is none worth offloading to.</summary>
    public GpuInfo? PrimaryGpu => Gpus.OrderByDescending(g => g.VramTotal ?? 0).FirstOrDefault();
}
