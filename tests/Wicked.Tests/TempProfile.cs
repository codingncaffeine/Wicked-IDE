using Wicked.Core;

namespace Wicked.Tests;

/// <summary>
/// A throwaway profile, as <c>WICKED_HOME</c> makes one: every folder under one temporary directory, removed afterwards.
/// The directory name is short because the daemon's socket lives inside it and a Unix socket path is limited to 107 bytes.
/// </summary>
internal sealed class TempProfile : IDisposable
{
    public TempProfile()
    {
        Root = Path.Combine(Path.GetTempPath(), "wk-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
        Paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? Root : null);
    }

    public string Root { get; }

    public WickedPaths Paths { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
