using System.Globalization;

namespace Wicked.Core.Hardware;

/// <summary>
/// Finds GPUs through <c>/sys/class/drm/card*/device</c>: vendor and device ids always, VRAM for AMD (the amdgpu driver
/// publishes <c>mem_info_vram_*</c>). Names come from the system's <c>pci.ids</c> when it is installed. NVIDIA cards are
/// reported here too, and the caller replaces them with NVML's richer answer when NVML loads.
/// </summary>
internal static class DrmScanner
{
    private static readonly string[] PciIdsLocations = ["/usr/share/hwdata/pci.ids", "/usr/share/misc/pci.ids", "/usr/share/pci.ids"];

    public static IReadOnlyList<GpuInfo> Scan(string sysRoot, string? pciIdsPath = null)
    {
        var drm = Path.Combine(sysRoot, "class", "drm");
        if (!Directory.Exists(drm)) return [];

        var result = new List<GpuInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var card in Directory.EnumerateDirectories(drm, "card*").OrderBy(d => d, StringComparer.Ordinal))
        {
            // card0-DP-1 and friends are connectors, not devices.
            var name = Path.GetFileName(card);
            if (name.Skip(4).Any(c => !char.IsAsciiDigit(c))) continue;
            var device = Path.Combine(card, "device");
            if (ReadHex(Path.Combine(device, "vendor")) is not { } vendor || ReadHex(Path.Combine(device, "device")) is not { } id) continue;

            var pci = string.Create(CultureInfo.InvariantCulture, $"{vendor:x4}:{id:x4}");
            var slot = ReadUevent(device, "PCI_SLOT_NAME") ?? name;
            if (!seen.Add(slot)) continue;

            var kind = VendorOf(vendor);
            var total = ReadLong(Path.Combine(device, "mem_info_vram_total"));
            var used = ReadLong(Path.Combine(device, "mem_info_vram_used"));
            long? free = total is { } t && used is { } u ? Math.Max(0, t - u) : null;
            result.Add(new GpuInfo(kind, LookupName(vendor, id, pciIdsPath) ?? $"{kind} GPU {pci}", total, free, null, null, pci));
        }

        return result;
    }

    public static GpuVendor VendorOf(int pciVendor) => pciVendor switch
    {
        0x10de => GpuVendor.Nvidia,
        0x1002 or 0x1022 => GpuVendor.Amd,
        0x8086 => GpuVendor.Intel,
        0x106b => GpuVendor.Apple,
        _ => GpuVendor.Other,
    };

    private static int? ReadHex(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return int.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static long? ReadLong(string path)
    {
        try
        {
            return long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadUevent(string device, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(device, "uevent")))
            {
                if (line.StartsWith(key + "=", StringComparison.Ordinal)) return line[(key.Length + 1)..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Reads only the needed vendor and device lines from pci.ids (about 1.5 MB), preferring the bracketed marketing name.</summary>
    internal static string? LookupName(int vendor, int device, string? pciIdsPath)
    {
        var path = pciIdsPath ?? PciIdsLocations.FirstOrDefault(File.Exists);
        if (path is null || !File.Exists(path)) return null;
        var vendorKey = vendor.ToString("x4", CultureInfo.InvariantCulture);
        var deviceKey = "\t" + device.ToString("x4", CultureInfo.InvariantCulture) + "  ";
        string? vendorName = null;
        var inVendor = false;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    if (inVendor) return null;
                    if (line.StartsWith(vendorKey + "  ", StringComparison.Ordinal))
                    {
                        inVendor = true;
                        vendorName = line[6..].Trim();
                    }
                }
                else if (inVendor && line.StartsWith(deviceKey, StringComparison.Ordinal))
                {
                    var name = line[deviceKey.Length..].Trim();
                    var open = name.LastIndexOf('[');
                    var close = name.LastIndexOf(']');
                    if (open >= 0 && close > open) name = name[(open + 1)..close];
                    return vendorName is null ? name : $"{ShortVendor(vendorName)} {name}";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read {path}: {ex.Message}");
        }

        return null;
    }

    private static string ShortVendor(string vendor) => vendor switch
    {
        var v when v.StartsWith("Advanced Micro Devices", StringComparison.Ordinal) => "AMD",
        var v when v.StartsWith("NVIDIA", StringComparison.Ordinal) => "NVIDIA",
        var v when v.StartsWith("Intel", StringComparison.Ordinal) => "Intel",
        _ => vendor,
    };
}
