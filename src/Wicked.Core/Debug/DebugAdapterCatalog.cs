using System.Runtime.InteropServices;
using Wicked.Core.Lsp;

namespace Wicked.Core.Debug;

/// <summary>How the client reaches an adapter once it runs.</summary>
public enum AdapterTransport
{
    /// <summary>Over the adapter's standard input and output.</summary>
    Stdio,

    /// <summary>The adapter listens on a port the client picks (the <c>{port}</c> in its arguments) and the client connects to it.</summary>
    Tcp,
}

/// <summary>Where an adapter comes from when it is not on the system.</summary>
public enum AdapterInstall
{
    /// <summary>Only from the system's package manager.</summary>
    System,

    /// <summary>A GitHub release asset per platform, unpacked into Wicked's own adapters folder.</summary>
    GitHubRelease,

    /// <summary>A Python package installed into Wicked's own adapters folder (needs Python and pip).</summary>
    Pip,
}

/// <summary>
/// A debug adapter the IDE knows how to find, start and install (D14): the <c>type</c> names a launch configuration may use
/// for it, the languages it serves, its program and arguments, and where it comes from.
/// </summary>
public sealed record DebugAdapterSpec(
    string Id,
    string Name,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> Arguments,
    AdapterTransport Transport,
    AdapterInstall Install,
    string? Package = null,
    string? Repository = null,
    IReadOnlyList<ReleaseAsset>? Assets = null,
    string? Homepage = null,
    string? Runtime = null,
    string? PythonModule = null)
{
    /// <summary>The packages that provide it on Arch, Debian/Ubuntu and Fedora, for the hint when it is missing.</summary>
    public IReadOnlyDictionary<string, string> SystemPackages { get; init; } = new Dictionary<string, string>();

    /// <summary>The same adapter described the way the language servers' locator and installer read it.</summary>
    public LanguageServerSpec AsServerSpec() => new(Id, Name, Languages, Programs, [], Install switch
    {
        AdapterInstall.GitHubRelease => InstallKind.GitHubRelease,
        _ => InstallKind.System,
    }, Package, Repository, Assets, Homepage);
}

/// <summary>The adapters the IDE knows. A configuration's <c>type</c> takes the first one in this list that is installed.</summary>
public static class DebugAdapterCatalog
{
    public static readonly IReadOnlyList<DebugAdapterSpec> All =
    [
        new("netcoredbg", "netcoredbg", ["coreclr", "netcoredbg", "clr"], ["csharp", "fsharp", "vb"], ["netcoredbg"], ["--interpreter=vscode"], AdapterTransport.Stdio,
            AdapterInstall.GitHubRelease, Repository: "Samsung/netcoredbg",
            Assets:
            [
                new("linux-x64", "netcoredbg-linux-amd64.tar.gz", "tar.gz", "netcoredbg/netcoredbg"),
                new("linux-arm64", "netcoredbg-linux-arm64.tar.gz", "tar.gz", "netcoredbg/netcoredbg"),
                new("win-x64", "netcoredbg-win64.zip", "zip", "netcoredbg/netcoredbg.exe"),
                new("osx-arm64", "netcoredbg-osx-arm64.zip", "zip", "netcoredbg/netcoredbg"),
            ],
            Homepage: "https://github.com/Samsung/netcoredbg")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "netcoredbg (AUR)" },
        },
        new("codelldb", "CodeLLDB", ["lldb", "codelldb"], ["c", "cpp", "rust", "zig", "objective-c", "objective-cpp", "swift"], ["codelldb"], ["--port", "{port}"], AdapterTransport.Tcp,
            AdapterInstall.GitHubRelease, Repository: "vadimcn/codelldb",
            Assets:
            [
                new("linux-x64", "codelldb-linux-x64.vsix", "zip", "extension/adapter/codelldb"),
                new("linux-arm64", "codelldb-linux-arm64.vsix", "zip", "extension/adapter/codelldb"),
                new("win-x64", "codelldb-win32-x64.vsix", "zip", "extension/adapter/codelldb.exe"),
                new("osx-arm64", "codelldb-darwin-arm64.vsix", "zip", "extension/adapter/codelldb"),
            ],
            Homepage: "https://github.com/vadimcn/codelldb"),
        new("debugpy", "debugpy", ["python", "debugpy"], ["python"], ["python3", "python"], ["-m", "debugpy.adapter"], AdapterTransport.Stdio,
            AdapterInstall.Pip, Package: "debugpy", Homepage: "https://github.com/microsoft/debugpy", PythonModule: "debugpy")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "python-debugpy", ["debian"] = "python3-debugpy", ["fedora"] = "python3-debugpy" },
        },
        new("js-debug", "JavaScript Debugger", ["pwa-node", "node", "pwa-chrome", "chrome", "pwa-msedge", "msedge", "node-terminal"], ["javascript", "typescript", "javascriptreact", "typescriptreact"],
            ["dapDebugServer.js"], ["{port}", "127.0.0.1"], AdapterTransport.Tcp,
            AdapterInstall.GitHubRelease, Repository: "microsoft/vscode-js-debug",
            Assets:
            [
                new("linux-x64", "js-debug-dap-v{version}.tar.gz", "tar.gz", "js-debug/src/dapDebugServer.js"),
                new("linux-arm64", "js-debug-dap-v{version}.tar.gz", "tar.gz", "js-debug/src/dapDebugServer.js"),
                new("win-x64", "js-debug-dap-v{version}.tar.gz", "tar.gz", "js-debug/src/dapDebugServer.js"),
                new("osx-arm64", "js-debug-dap-v{version}.tar.gz", "tar.gz", "js-debug/src/dapDebugServer.js"),
            ],
            Homepage: "https://github.com/microsoft/vscode-js-debug", Runtime: "node"),
        new("gdb", "GDB", ["gdb"], ["c", "cpp", "rust", "zig"], ["gdb"], ["--interpreter=dap"], AdapterTransport.Stdio, AdapterInstall.System, Homepage: "https://sourceware.org/gdb")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "gdb", ["debian"] = "gdb", ["fedora"] = "gdb" },
        },
        new("lldb-dap", "lldb-dap", ["lldb-dap"], ["c", "cpp", "rust", "swift"], ["lldb-dap", "lldb-vscode"], [], AdapterTransport.Stdio, AdapterInstall.System, Homepage: "https://lldb.llvm.org")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "lldb", ["debian"] = "lldb", ["fedora"] = "lldb" },
        },
    ];

    /// <summary>The adapters a launch configuration of <paramref name="type"/> may use, in order of preference.</summary>
    public static IEnumerable<DebugAdapterSpec> ForType(string type) => All.Where(s => s.Types.Contains(type, StringComparer.OrdinalIgnoreCase));

    /// <summary>The adapters that debug <paramref name="languageId"/>, in order of preference.</summary>
    public static IEnumerable<DebugAdapterSpec> ForLanguage(string languageId) => All.Where(s => s.Languages.Contains(languageId));

    public static DebugAdapterSpec? ById(string id) => All.FirstOrDefault(s => s.Id == id);
}

/// <summary>What to run for an adapter: the program, its arguments, extra environment, and how to reach it.</summary>
public sealed record AdapterCommand(string Program, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment, AdapterTransport Transport);

/// <summary>
/// Finds an adapter's program: what Wicked installed into its adapters folder first, then the system's PATH. Programs that run
/// under a runtime (js-debug under Node, debugpy under Python) are found together with it.
/// </summary>
public sealed class DebugAdapterLocator(string adaptersFolder, Func<string, string?>? env = null)
{
    private readonly Func<string, string?> _env = env ?? System.Environment.GetEnvironmentVariable;
    private readonly ServerLocator _servers = new(adaptersFolder, env);

    public string AdaptersFolder { get; } = adaptersFolder;

    /// <summary>The command to run <paramref name="spec"/> here, or null when it is not installed.</summary>
    public AdapterCommand? Find(DebugAdapterSpec spec)
    {
        if (spec.PythonModule is not null) return FindPython(spec);
        if (_servers.Find(spec.AsServerSpec()) is not { } found) return null;
        if (spec.Runtime is { } runtime)
        {
            var node = _servers.Find(new LanguageServerSpec(runtime, runtime, [], [runtime], [], InstallKind.System));
            return node is null ? null : new AdapterCommand(node.Executable, [found.Executable, .. spec.Arguments], Empty, spec.Transport);
        }

        return new AdapterCommand(found.Executable, spec.Arguments, Empty, spec.Transport);
    }

    /// <summary>Python with the adapter's own folder on its path when Wicked installed debugpy there.</summary>
    private AdapterCommand? FindPython(DebugAdapterSpec spec)
    {
        var lib = Path.Combine(AdaptersFolder, spec.Id, "lib");
        if (!Directory.Exists(lib)) return null;
        var python = _servers.Find(new LanguageServerSpec("python", "Python", [], spec.Programs, [], InstallKind.System));
        return python is null ? null : new AdapterCommand(python.Executable, spec.Arguments, new Dictionary<string, string> { ["PYTHONPATH"] = lib }, spec.Transport);
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    /// <summary>
    /// Like <see cref="Find"/>, and for an adapter that is a Python module also asks the system's Python whether it has the module
    /// (<c>python3 -m debugpy --version</c>); runs a process, so call it from a worker.
    /// </summary>
    public async Task<AdapterCommand?> FindAsync(DebugAdapterSpec spec, CancellationToken ct = default)
    {
        if (Find(spec) is { } found) return found;
        if (spec.PythonModule is not { } module) return null;
        var python = _servers.Find(new LanguageServerSpec("python", "Python", [], spec.Programs, [], InstallKind.System));
        if (python is null) return null;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(8));
            var info = new System.Diagnostics.ProcessStartInfo(python.Executable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-m");
            info.ArgumentList.Add(module);
            info.ArgumentList.Add("--version");
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null) return null;
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? new AdapterCommand(python.Executable, spec.Arguments, Empty, spec.Transport) : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Which of the catalog's adapters are installed, for the pickers (Python's module is not probed here).</summary>
    public IReadOnlyList<DebugAdapterSpec> Installed() => [.. DebugAdapterCatalog.All.Where(s => Find(s) is not null)];

    /// <summary>The system's package family for the hint about a missing adapter: arch, debian or fedora.</summary>
    public static string? PackageFamily()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;
        try
        {
            var release = File.ReadAllText("/etc/os-release").ToLowerInvariant();
            if (release.Contains("arch", StringComparison.Ordinal)) return "arch";
            if (release.Contains("debian", StringComparison.Ordinal) || release.Contains("ubuntu", StringComparison.Ordinal)) return "debian";
            if (release.Contains("fedora", StringComparison.Ordinal)) return "fedora";
        }
        catch (IOException)
        {
        }

        return null;
    }
}

/// <summary>A debug adapter could not be found for a configuration's type; the spec (when the catalog has one) says what would install it.</summary>
public sealed class DebugAdapterMissingException(string type, DebugAdapterSpec? spec, string message) : IOException(message)
{
    public string Type { get; } = type;

    public DebugAdapterSpec? Spec { get; } = spec;
}

/// <summary>
/// Installs debug adapters into Wicked's own adapters folder, never system-wide (D14): a GitHub release asset checked against
/// the sha256 GitHub publishes for it (the language servers' installer does the download), or a Python package installed with
/// pip into the adapter's own folder. Everything runs on workers; progress is reported as lines.
/// </summary>
public sealed class DebugAdapterInstaller(HttpClient http, string adaptersFolder, Uri? githubApi = null)
{
    private readonly LanguageServerInstaller _releases = new(http, adaptersFolder, githubApi);

    /// <summary>Why this adapter cannot be installed here (no Python, only from the system's packages), or null when it can.</summary>
    public static string? Unavailable(DebugAdapterSpec spec, Func<string, string?>? env = null)
    {
        var locator = new ServerLocator("", env);
        string? Need(string program, string what) =>
            locator.Find(new LanguageServerSpec("tool", program, [], [program], [], InstallKind.System)) is null ? $"{spec.Name} installs with {what}, which is not installed" : null;
        switch (spec.Install)
        {
            case AdapterInstall.Pip:
                return locator.Find(new LanguageServerSpec("python", "Python", [], spec.Programs, [], InstallKind.System)) is null ? $"{spec.Name} installs with Python and pip, which are not installed" : null;
            case AdapterInstall.System:
                var family = DebugAdapterLocator.PackageFamily();
                var package = family is not null && spec.SystemPackages.TryGetValue(family, out var p) ? $": {p}" : spec.SystemPackages.Count == 0 ? "" : ": " + string.Join(", ", spec.SystemPackages.Select(kv => $"{kv.Key} {kv.Value}"));
                return $"{spec.Name} comes from your system's packages{package}";
            default:
                return spec.Runtime is { } runtime ? Need(runtime, $"{runtime} to run") : null;
        }
    }

    public async Task InstallAsync(DebugAdapterSpec spec, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (Unavailable(spec) is { } why && spec.Install != AdapterInstall.GitHubRelease) throw new ServerInstallException(why);
        var folder = Path.Combine(adaptersFolder, spec.Id);
        Directory.CreateDirectory(folder);
        switch (spec.Install)
        {
            case AdapterInstall.GitHubRelease:
                await _releases.InstallAsync(spec.AsServerSpec(), progress, ct).ConfigureAwait(false);
                break;
            case AdapterInstall.Pip:
                var python = new ServerLocator("", null).Find(new LanguageServerSpec("python", "Python", [], spec.Programs, [], InstallKind.System))!.Executable;
                await LanguageServerInstaller.RunAsync(python, ["-m", "pip", "install", "--target", Path.Combine(folder, "lib"), "--upgrade", "--no-input", spec.Package!], folder, progress, ct).ConfigureAwait(false);
                break;
            default:
                throw new ServerInstallException(Unavailable(spec) ?? $"{spec.Name} comes from your system's packages");
        }

        if (new DebugAdapterLocator(adaptersFolder, _ => null).Find(spec) is null && spec.Runtime is null && spec.PythonModule is null) throw new ServerInstallException($"{spec.Name} installed, but its program was not found in {folder}");
        progress?.Report($"{spec.Name} is installed.");
    }
}
