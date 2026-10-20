namespace Wicked.Core;

/// <summary>
/// Where Wicked keeps things. Each platform's own folders by default (XDG on Linux, AppData on Windows, Library on macOS);
/// <c>WICKED_HOME</c> puts everything under one folder instead, which is how tests and smoke runs keep away from the
/// user's real profile.
/// </summary>
/// <param name="Config">Settings, keybindings, themes and the user's own scripts.</param>
/// <param name="Data">The model store, installed engines and conversations.</param>
/// <param name="State">Logs and what the app remembers about its last run.</param>
/// <param name="Cache">Anything that can be fetched or rebuilt again.</param>
/// <param name="Runtime">The daemon's socket and lock; gone after a reboot on Linux.</param>
/// <param name="Isolated">True when <c>WICKED_HOME</c> chose the folders.</param>
public sealed record WickedPaths(string Config, string Data, string State, string Cache, string Runtime, bool Isolated)
{
    public const string HomeVariable = "WICKED_HOME";

    /// <summary>A Unix socket path is limited to 107 bytes on Linux (sun_path is 108 with the terminator), 103 on macOS.</summary>
    public const int MaxSocketPath = 103;

    public string Logs => Path.Combine(State, "logs");

    public string DaemonSocket => Path.Combine(Runtime, "wickedd.sock");

    public string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>The folders for this user, read through <paramref name="env"/> so a test can supply its own environment.</summary>
    public static WickedPaths Resolve(Func<string, string?> env)
    {
        if (env(HomeVariable) is { Length: > 0 } home)
        {
            var root = Path.GetFullPath(home);
            return new WickedPaths(
                Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "state"),
                Path.Combine(root, "cache"), Path.Combine(root, "run"), Isolated: true);
        }

        // DoNotVerify: on a fresh account the folder may not exist yet, and without it .NET returns "" on Linux.
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        if (OperatingSystem.IsWindows())
        {
            var roaming = env("APPDATA") is { Length: > 0 } a ? a : Path.Combine(user, "AppData", "Roaming");
            var local = env("LOCALAPPDATA") is { Length: > 0 } l ? l : Path.Combine(user, "AppData", "Local");
            var root = Path.Combine(local, "Wicked");
            return new WickedPaths(
                Path.Combine(roaming, "Wicked"), Path.Combine(root, "data"), Path.Combine(root, "state"),
                Path.Combine(root, "cache"), Path.Combine(root, "run"), Isolated: false);
        }

        if (OperatingSystem.IsMacOS())
        {
            var support = Path.Combine(user, "Library", "Application Support", "Wicked");
            return new WickedPaths(
                Path.Combine(support, "config"), Path.Combine(support, "data"), Path.Combine(user, "Library", "Logs", "Wicked"),
                Path.Combine(user, "Library", "Caches", "Wicked"), Path.Combine(support, "run"), Isolated: false);
        }

        string Xdg(string variable, params string[] fallback) =>
            env(variable) is { Length: > 0 } v && Path.IsPathRooted(v) ? v : Path.Combine([user, .. fallback]);

        var state = Path.Combine(Xdg("XDG_STATE_HOME", ".local", "state"), "wicked");
        var runtime = env("XDG_RUNTIME_DIR") is { Length: > 0 } r && Path.IsPathRooted(r)
            ? Path.Combine(r, "wicked")
            : Path.Combine(state, "run");
        return new WickedPaths(
            Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "wicked"),
            Path.Combine(Xdg("XDG_DATA_HOME", ".local", "share"), "wicked"),
            state,
            Path.Combine(Xdg("XDG_CACHE_HOME", ".cache"), "wicked"),
            runtime,
            Isolated: false);
    }

    /// <summary>The folders for the current process's environment.</summary>
    public static WickedPaths ForCurrentUser() => Resolve(Environment.GetEnvironmentVariable);

    /// <summary>Creates every folder; the runtime folder is private to the user, as the socket inside it is.</summary>
    public void EnsureCreated()
    {
        foreach (var dir in new[] { Config, Data, State, Cache, Logs }) Directory.CreateDirectory(dir);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(Runtime);
        }
        else
        {
            Directory.CreateDirectory(Runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
