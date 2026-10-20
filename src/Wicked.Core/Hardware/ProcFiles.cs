using System.Globalization;

namespace Wicked.Core.Hardware;

/// <summary>Parsers for <c>/proc/cpuinfo</c> and <c>/proc/meminfo</c>, kept pure so a test can feed them text.</summary>
internal static class ProcFiles
{
    public static CpuInfo? ParseCpuInfo(string text)
    {
        string? model = null;
        var cores = 0;
        var avx2 = false;
        var avx512 = false;
        foreach (var raw in text.Split('\n'))
        {
            var colon = raw.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var key = raw[..colon].Trim();
            var value = raw[(colon + 1)..].Trim();
            switch (key)
            {
                case "processor":
                    cores++;
                    break;
                case "model name" or "Model" or "cpu model" when model is null:
                    model = value;
                    break;
                case "flags" or "Features":
                    var flags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    avx2 |= flags.Contains("avx2");
                    avx512 |= flags.Contains("avx512f");
                    break;
            }
        }

        return cores == 0 ? null : new CpuInfo(model ?? "Unknown CPU", cores, avx2, avx512);
    }

    /// <summary>Returns (total, available) in bytes; <c>MemAvailable</c> falls back to free + buffers + cached on very old kernels.</summary>
    public static (long Total, long Available)? ParseMemInfo(string text)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0) continue;
            var parts = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                values[line[..colon]] = parts.Length > 1 && parts[1] == "kB" ? n * 1024 : n;
            }
        }

        if (!values.TryGetValue("MemTotal", out var total)) return null;
        if (!values.TryGetValue("MemAvailable", out var available))
        {
            available = values.GetValueOrDefault("MemFree") + values.GetValueOrDefault("Buffers") + values.GetValueOrDefault("Cached");
        }

        return (total, available);
    }
}
