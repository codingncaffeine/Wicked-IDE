using System.Runtime.InteropServices;
using System.Text;

namespace Wicked.Core.Hardware;

/// <summary>
/// NVIDIA's management library, bound at run time with <see cref="NativeLibrary.TryLoad(string, out nint)"/> so a machine
/// without an NVIDIA driver simply has no NVML and nothing throws. The library is initialised, read and shut down within
/// one <see cref="Query"/> call; nothing stays loaded.
/// </summary>
internal static class Nvml
{
    private const int Success = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] BusIdLegacy;
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] BusId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ShutdownFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountFn(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int HandleFn(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NameFn(nint device, byte[] name, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int MemoryFn(nint device, out MemoryInfo memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PciFn(nint device, out PciInfo pci);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int StringFn(byte[] text, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IntFn(out int value);

    private static string[] Candidates => OperatingSystem.IsWindows()
        ? ["nvml.dll", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll")]
        : ["libnvidia-ml.so.1", "libnvidia-ml.so"];

    /// <summary>Returns the NVIDIA GPUs NVML reports, or an empty list when the library or the driver is not there.</summary>
    public static IReadOnlyList<GpuInfo> Query(string[]? libraryNames = null)
    {
        nint lib = 0;
        foreach (var name in libraryNames ?? Candidates)
        {
            if (NativeLibrary.TryLoad(name, out lib)) break;
            lib = 0;
        }

        if (lib == 0) return [];

        var initialised = false;
        T? Bind<T>(string symbol)
            where T : Delegate => NativeLibrary.TryGetExport(lib, symbol, out var p) ? Marshal.GetDelegateForFunctionPointer<T>(p) : null;

        ShutdownFn? shutdown = null;
        try
        {
            var init = Bind<InitFn>("nvmlInit_v2");
            shutdown = Bind<ShutdownFn>("nvmlShutdown");
            var count = Bind<CountFn>("nvmlDeviceGetCount_v2");
            var handle = Bind<HandleFn>("nvmlDeviceGetHandleByIndex_v2");
            var nameOf = Bind<NameFn>("nvmlDeviceGetName");
            var memory = Bind<MemoryFn>("nvmlDeviceGetMemoryInfo");
            if (init is null || count is null || handle is null || nameOf is null || memory is null)
            {
                Log.Warn("NVML loaded but lacks the symbols we need");
                return [];
            }

            if (init() != Success)
            {
                Log.Warn("nvmlInit_v2 failed (no NVIDIA driver loaded?)");
                return [];
            }

            initialised = true;
            string? driver = null;
            if (Bind<StringFn>("nvmlSystemGetDriverVersion") is { } driverFn)
            {
                var buffer = new byte[96];
                if (driverFn(buffer, (uint)buffer.Length) == Success) driver = Text(buffer);
            }

            Version? cuda = null;
            if (Bind<IntFn>("nvmlSystemGetCudaDriverVersion_v2") is { } cudaFn && cudaFn(out var raw) == Success)
            {
                cuda = new Version(raw / 1000, raw % 1000 / 10);
            }

            var pciFn = Bind<PciFn>("nvmlDeviceGetPciInfo_v3");
            var result = new List<GpuInfo>();
            if (count(out var n) != Success) return [];
            for (uint i = 0; i < n; i++)
            {
                if (handle(i, out var device) != Success) continue;
                var nameBuffer = new byte[96];
                var gpuName = nameOf(device, nameBuffer, (uint)nameBuffer.Length) == Success ? Text(nameBuffer) : "NVIDIA GPU";
                long? total = null;
                long? free = null;
                if (memory(device, out var mem) == Success)
                {
                    total = (long)mem.Total;
                    free = (long)mem.Free;
                }

                string? pci = null;
                if (pciFn is not null && pciFn(device, out var info) == Success)
                {
                    pci = $"{info.PciDeviceId & 0xffff:x4}:{info.PciDeviceId >> 16:x4}";
                }

                result.Add(new GpuInfo(GpuVendor.Nvidia, gpuName, total, free, driver, cuda, pci));
            }

            return result;
        }
        catch (Exception ex) when (ex is MarshalDirectiveException or SEHException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"NVML failed: {ex.Message}");
            return [];
        }
        finally
        {
            if (initialised) shutdown?.Invoke();
            NativeLibrary.Free(lib);
        }
    }

    private static string Text(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }
}
