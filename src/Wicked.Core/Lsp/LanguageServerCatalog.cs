namespace Wicked.Core.Lsp;

/// <summary>How a server that is not on the system gets installed.</summary>
public enum InstallKind
{
    /// <summary>Only from the system's package manager; the catalog says which package.</summary>
    System,

    /// <summary>A GitHub release asset per platform, unpacked into Wicked's own servers folder.</summary>
    GitHubRelease,

    /// <summary>An npm package installed into Wicked's servers folder (needs Node.js).</summary>
    Npm,

    /// <summary>A .NET global tool installed into Wicked's servers folder (needs the .NET SDK).</summary>
    DotnetTool,

    /// <summary><c>go install</c> into Wicked's servers folder (needs Go).</summary>
    Go,
}

/// <summary>
/// A release asset of a GitHub-released server: the file name pattern (<c>{version}</c> stands for the tag without a
/// leading v), how it is packed, and where the program sits inside it.
/// </summary>
public sealed record ReleaseAsset(string Platform, string Pattern, string Packing, string Program);

/// <summary>
/// A language server the IDE knows how to find, start and install (D14): which languages it serves, the program names it
/// goes by, the arguments it takes, and the package that provides it on common systems.
/// </summary>
public sealed record LanguageServerSpec(
    string Id,
    string Name,
    IReadOnlyList<string> Languages,
    IReadOnlyList<string> Programs,
    IReadOnlyList<string> Arguments,
    InstallKind Install,
    string? Package = null,
    string? Repository = null,
    IReadOnlyList<ReleaseAsset>? Assets = null,
    string? Homepage = null,
    IReadOnlyList<string>? RootMarkers = null)
{
    /// <summary>The packages that provide it on Arch, Debian/Ubuntu and Fedora, for the hint when it is missing.</summary>
    public IReadOnlyDictionary<string, string> SystemPackages { get; init; } = new Dictionary<string, string>();
}

/// <summary>The servers the IDE knows. Each language takes the first one in this list that is installed.</summary>
public static class LanguageServerCatalog
{
    private static readonly IReadOnlyList<LanguageServerSpec> BuiltIn =
    [
        new("clangd", "clangd", ["c", "cpp", "objective-c", "objective-cpp", "cuda-cpp"], ["clangd"], ["--background-index", "--header-insertion=iwyu", "--completion-style=detailed"],
            InstallKind.GitHubRelease, Repository: "clangd/clangd",
            Assets: [new("linux-x64", "clangd-linux-{version}.zip", "zip", "clangd_{version}/bin/clangd"), new("win-x64", "clangd-windows-{version}.zip", "zip", "clangd_{version}/bin/clangd.exe"), new("osx-arm64", "clangd-mac-{version}.zip", "zip", "clangd_{version}/bin/clangd")],
            Homepage: "https://clangd.llvm.org", RootMarkers: ["compile_commands.json", "compile_flags.txt", ".clangd", "CMakeLists.txt"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "clang", ["debian"] = "clangd", ["fedora"] = "clang-tools-extra" },
        },
        new("rust-analyzer", "rust-analyzer", ["rust"], ["rust-analyzer"], [],
            InstallKind.GitHubRelease, Repository: "rust-lang/rust-analyzer",
            Assets: [new("linux-x64", "rust-analyzer-x86_64-unknown-linux-gnu.gz", "gz", "rust-analyzer"), new("linux-arm64", "rust-analyzer-aarch64-unknown-linux-gnu.gz", "gz", "rust-analyzer"), new("win-x64", "rust-analyzer-x86_64-pc-windows-msvc.zip", "zip", "rust-analyzer.exe"), new("osx-arm64", "rust-analyzer-aarch64-apple-darwin.gz", "gz", "rust-analyzer")],
            Homepage: "https://rust-analyzer.github.io", RootMarkers: ["Cargo.toml"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "rust-analyzer", ["debian"] = "rust-analyzer", ["fedora"] = "rust-analyzer" },
        },
        new("csharp-ls", "csharp-ls", ["csharp"], ["csharp-ls"], [], InstallKind.DotnetTool, Package: "csharp-ls",
            Homepage: "https://github.com/razzmatazz/csharp-language-server", RootMarkers: ["*.sln", "*.slnx", "*.csproj"]),
        new("pyright", "Pyright", ["python"], ["basedpyright-langserver", "pyright-langserver"], ["--stdio"], InstallKind.Npm, Package: "pyright",
            Homepage: "https://github.com/microsoft/pyright", RootMarkers: ["pyproject.toml", "setup.py", "requirements.txt"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "pyright" },
        },
        new("typescript-language-server", "TypeScript", ["typescript", "typescriptreact", "javascript", "javascriptreact"], ["typescript-language-server"], ["--stdio"],
            InstallKind.Npm, Package: "typescript-language-server typescript", Homepage: "https://github.com/typescript-language-server/typescript-language-server", RootMarkers: ["package.json", "tsconfig.json", "jsconfig.json"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "typescript-language-server" },
        },
        new("gopls", "gopls", ["go"], ["gopls"], [], InstallKind.Go, Package: "golang.org/x/tools/gopls@latest", Homepage: "https://go.dev/gopls", RootMarkers: ["go.mod", "go.work"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "gopls", ["debian"] = "gopls", ["fedora"] = "golang-x-tools-gopls" },
        },
        new("lua-language-server", "Lua", ["lua"], ["lua-language-server"], [],
            InstallKind.GitHubRelease, Repository: "LuaLS/lua-language-server",
            Assets: [new("linux-x64", "lua-language-server-{version}-linux-x64.tar.gz", "tar.gz", "bin/lua-language-server"), new("linux-arm64", "lua-language-server-{version}-linux-arm64.tar.gz", "tar.gz", "bin/lua-language-server"), new("win-x64", "lua-language-server-{version}-win32-x64.zip", "zip", "bin/lua-language-server.exe"), new("osx-arm64", "lua-language-server-{version}-darwin-arm64.tar.gz", "tar.gz", "bin/lua-language-server")],
            Homepage: "https://luals.github.io", RootMarkers: [".luarc.json"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "lua-language-server" },
        },
        new("bash-language-server", "Bash", ["shellscript"], ["bash-language-server"], ["start"], InstallKind.Npm, Package: "bash-language-server", Homepage: "https://github.com/bash-lsp/bash-language-server")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "bash-language-server" },
        },
        new("json", "JSON", ["json", "jsonc"], ["vscode-json-language-server", "vscode-json-languageserver"], ["--stdio"], InstallKind.Npm, Package: "vscode-langservers-extracted", Homepage: "https://github.com/hrsh7th/vscode-langservers-extracted")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "vscode-json-languageserver" },
        },
        new("html", "HTML", ["html"], ["vscode-html-language-server"], ["--stdio"], InstallKind.Npm, Package: "vscode-langservers-extracted", Homepage: "https://github.com/hrsh7th/vscode-langservers-extracted"),
        new("css", "CSS", ["css", "scss", "less"], ["vscode-css-language-server"], ["--stdio"], InstallKind.Npm, Package: "vscode-langservers-extracted", Homepage: "https://github.com/hrsh7th/vscode-langservers-extracted"),
        new("yaml", "YAML", ["yaml", "dockercompose"], ["yaml-language-server"], ["--stdio"], InstallKind.Npm, Package: "yaml-language-server", Homepage: "https://github.com/redhat-developer/yaml-language-server")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "yaml-language-server" },
        },
        new("marksman", "Marksman", ["markdown"], ["marksman"], ["server"],
            InstallKind.GitHubRelease, Repository: "artempyanykh/marksman",
            Assets: [new("linux-x64", "marksman-linux-x64", "raw", "marksman"), new("linux-arm64", "marksman-linux-arm64", "raw", "marksman"), new("win-x64", "marksman.exe", "raw", "marksman.exe"), new("osx-arm64", "marksman-macos", "raw", "marksman")],
            Homepage: "https://github.com/artempyanykh/marksman"),
        new("taplo", "Taplo", ["toml"], ["taplo"], ["lsp", "stdio"],
            InstallKind.GitHubRelease, Repository: "tamasfe/taplo",
            Assets: [new("linux-x64", "taplo-linux-x86_64.gz", "gz", "taplo"), new("linux-arm64", "taplo-linux-aarch64.gz", "gz", "taplo"), new("win-x64", "taplo-windows-x86_64.zip", "zip", "taplo.exe"), new("osx-arm64", "taplo-darwin-aarch64.gz", "gz", "taplo")],
            Homepage: "https://taplo.tamasfe.dev")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "taplo-cli" },
        },
        new("zls", "ZLS", ["zig"], ["zls"], [], InstallKind.System, Package: "zls", Homepage: "https://github.com/zigtools/zls")
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "zls" },
        },
        new("jdtls", "Eclipse JDT LS", ["java"], ["jdtls"], [], InstallKind.System, Package: "jdtls", Homepage: "https://github.com/eclipse-jdtls/eclipse.jdt.ls", RootMarkers: ["pom.xml", "build.gradle", "build.gradle.kts"])
        {
            SystemPackages = new Dictionary<string, string> { ["arch"] = "jdtls" },
        },
        new("dockerfile", "Dockerfile", ["dockerfile"], ["docker-langserver"], ["--stdio"], InstallKind.Npm, Package: "dockerfile-language-server-nodejs", Homepage: "https://github.com/rcjsuen/dockerfile-language-server"),
        new("cmake", "CMake", ["cmake"], ["neocmakelsp", "cmake-language-server"], [], InstallKind.System, Package: "neocmakelsp", Homepage: "https://github.com/neocmakelsp/neocmakelsp"),
        new("intelephense", "PHP (Intelephense)", ["php"], ["intelephense"], ["--stdio"], InstallKind.Npm, Package: "intelephense", Homepage: "https://intelephense.com"),
        new("ruby-lsp", "Ruby LSP", ["ruby"], ["ruby-lsp"], [], InstallKind.System, Package: "ruby-lsp", Homepage: "https://github.com/Shopify/ruby-lsp"),
    ];

    private static readonly Lock Gate = new();
    private static IReadOnlyList<(string Owner, LanguageServerSpec Spec)> _added = [];
    private static IReadOnlyList<LanguageServerSpec> _all = BuiltIn;

    /// <summary>Every server: those extensions added first (so an installed extension's server is tried ahead of the built-in one), then the built-in ones.</summary>
    public static IReadOnlyList<LanguageServerSpec> All => Volatile.Read(ref _all);

    /// <summary>Adds a server for <paramref name="owner"/> (an extension id); disposing the handle takes it back. A second server with the same id replaces the first.</summary>
    public static IDisposable Register(string owner, LanguageServerSpec spec)
    {
        lock (Gate)
        {
            _added = [.. _added.Where(a => a.Spec.Id != spec.Id), (owner, spec)];
            Rebuild();
        }

        return new Registration(owner, spec);
    }

    /// <summary>Takes back every server <paramref name="owner"/> added.</summary>
    public static void Unregister(string owner)
    {
        lock (Gate)
        {
            _added = [.. _added.Where(a => a.Owner != owner)];
            Rebuild();
        }
    }

    private static void Rebuild() =>
        Volatile.Write(ref _all, [.. _added.Select(a => a.Spec), .. BuiltIn.Where(b => _added.All(a => a.Spec.Id != b.Id))]);

    private sealed class Registration(string owner, LanguageServerSpec spec) : IDisposable
    {
        public void Dispose()
        {
            lock (Gate)
            {
                _added = [.. _added.Where(a => !(a.Owner == owner && ReferenceEquals(a.Spec, spec)))];
                Rebuild();
            }
        }
    }

    /// <summary>The servers that serve <paramref name="languageId"/>, in order of preference.</summary>
    public static IEnumerable<LanguageServerSpec> For(string languageId) => All.Where(s => s.Languages.Contains(languageId));

    public static LanguageServerSpec? ById(string id) => All.FirstOrDefault(s => s.Id == id);
}
