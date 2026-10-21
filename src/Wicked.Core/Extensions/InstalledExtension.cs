namespace Wicked.Core.Extensions;

public enum ExtensionState
{
    /// <summary>Turned off by the user; none of its files are in use.</summary>
    Disabled,

    /// <summary>Enabled and waiting for its activation event (a language opening).</summary>
    Inactive,

    Active,

    /// <summary>Its code threw while activating, or took too long; it stays off until the next start or until it is enabled again.</summary>
    Failed,

    /// <summary>Its manifest cannot be used (unreadable, or for another API version); <see cref="InstalledExtension.Problem"/> says why.</summary>
    Broken,
}

/// <summary>An extension in the extensions folder, as the Extensions panel shows it.</summary>
/// <param name="Folder">Where its files are.</param>
/// <param name="Manifest">Its manifest, or null when it is <see cref="ExtensionState.Broken"/>.</param>
/// <param name="State">Whether it is off, waiting, running or failed.</param>
/// <param name="Problem">Why it is failed or broken, in words.</param>
/// <param name="LoadMilliseconds">Time to load its assembly and find its main type (0 when none).</param>
/// <param name="ActivateMilliseconds">Time its <c>ActivateAsync</c> took.</param>
/// <param name="Provides">What it offers now, one line each ("2 themes", "command Hello: Say Hello").</param>
public sealed record InstalledExtension(
    string Folder,
    ExtensionManifest? Manifest,
    ExtensionState State,
    string? Problem,
    double LoadMilliseconds,
    double ActivateMilliseconds,
    IReadOnlyList<string> Provides)
{
    /// <summary><c>publisher.id</c>, or the folder's name for a broken extension.</summary>
    public string Id => Manifest?.FullId ?? Path.GetFileName(Folder);

    public string Name => Manifest?.Name ?? Id;

    public bool RunsCode => Manifest?.Main is not null;
}

/// <summary>What the user is asked before an extension is installed.</summary>
/// <param name="Manifest">The extension that would be installed.</param>
/// <param name="Source">Where it comes from (a folder, a file, <c>github.com/owner/repo</c>).</param>
/// <param name="Verified">Whether a published checksum matched the download; false for a folder, a local file, or a release with no checksum.</param>
public sealed record InstallRequest(ExtensionManifest Manifest, string Source, bool Verified);

/// <summary>An extension could not be installed; the message says why in the user's terms.</summary>
public sealed class ExtensionInstallException(string message, Exception? inner = null) : IOException(message, inner);
