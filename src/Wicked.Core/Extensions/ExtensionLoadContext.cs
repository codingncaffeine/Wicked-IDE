using System.Reflection;
using System.Runtime.Loader;

namespace Wicked.Core.Extensions;

/// <summary>
/// The load context one extension lives in. It is collectible, so disabling or uninstalling the extension lets its
/// assemblies go. The host's own assemblies (Wicked, Avalonia, the framework) always come from the default context, so a
/// type means the same thing on both sides; everything else is looked up in the extension's folder first. Assemblies are
/// read into memory and loaded from there, so no file stays open and an uninstall can delete the folder on Windows.
/// </summary>
internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private static readonly string[] Shared =
    [
        "Wicked.", "Avalonia", "System.", "Microsoft.CSharp", "Microsoft.Win32.", "netstandard", "mscorlib", "WindowsBase",
        "SkiaSharp", "HarfBuzzSharp", "TextMateSharp", "CommunityToolkit.Mvvm", "Markdig",
    ];

    private readonly string _folder;
    private readonly AssemblyDependencyResolver? _resolver;

    public ExtensionLoadContext(string folder, string mainPath, string name)
        : base("extension:" + name, isCollectible: true)
    {
        _folder = folder;
        _resolver = File.Exists(Path.ChangeExtension(mainPath, ".deps.json")) ? new AssemblyDependencyResolver(mainPath) : null;
    }

    public Assembly LoadMain(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null || Shared.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))) return null;
        var path = _resolver?.ResolveAssemblyToPath(assemblyName);
        if (path is null || !File.Exists(path)) path = Path.Combine(_folder, name + ".dll");
        if (!File.Exists(path)) return null;
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return LoadFromStream(stream);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}
