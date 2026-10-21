using Wicked.Core.Git;

namespace Wicked.Tests.Git;

/// <summary>
/// A repository in a folder of its own under the worktree's <c>scratch/</c>, with a home folder and a global git configuration
/// of its own (HOME, GIT_CONFIG_GLOBAL and GIT_CONFIG_NOSYSTEM set), so no test ever reads or changes the user's git setup. The
/// author is <c>Test &lt;test@example.invalid&gt;</c>.
/// </summary>
internal sealed class GitSandbox : IDisposable
{
    private GitSandbox(string root)
    {
        Root = root;
        Home = Path.Combine(root, "home");
        Directory.CreateDirectory(Home);
        var config = Path.Combine(Home, "gitconfig");
        File.WriteAllText(config, "[user]\n\tname = Test\n\temail = test@example.invalid\n[init]\n\tdefaultBranch = main\n[commit]\n\tgpgsign = false\n[core]\n\tautocrlf = false\n[protocol \"file\"]\n\tallow = always\n[advice]\n\tdetachedHead = false\n");
        Env = new GitEnvironment
        {
            Timeout = TimeSpan.FromSeconds(60),
            Variables = new Dictionary<string, string?>
            {
                ["HOME"] = Home,
                ["XDG_CONFIG_HOME"] = Path.Combine(Home, "xdg"),
                ["GIT_CONFIG_GLOBAL"] = config,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_AUTHOR_NAME"] = "Test",
                ["GIT_AUTHOR_EMAIL"] = "test@example.invalid",
                ["GIT_COMMITTER_NAME"] = "Test",
                ["GIT_COMMITTER_EMAIL"] = "test@example.invalid",
                ["GIT_TERMINAL_PROMPT"] = "0",
                // The scratch folder lives inside the worktree's own repository; git must never look that far up.
                ["GIT_CEILING_DIRECTORIES"] = ScratchRoot(),
            },
        };
        RepoPath = Path.Combine(root, "repo");
        Directory.CreateDirectory(RepoPath);
    }

    public string Root { get; }

    public string Home { get; }

    public string RepoPath { get; }

    public GitEnvironment Env { get; }

    /// <summary>The worktree's scratch folder (gitignored), found from the test assembly up to the solution.</summary>
    public static string ScratchRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) dir = dir.Parent;
        var scratch = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Wicked.slnx not found above the test assembly"), "scratch", "git-tests");
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    /// <summary>A sandbox with an initialised, empty repository.</summary>
    public static async Task<(GitSandbox Sandbox, GitRepository Repo)> CreateAsync()
    {
        var sandbox = new GitSandbox(Path.Combine(ScratchRoot(), Guid.NewGuid().ToString("N")[..10]));
        var repo = await GitRepository.InitAsync(sandbox.RepoPath, sandbox.Env);
        return (sandbox, repo ?? throw new InvalidOperationException("git init made no repository"));
    }

    public static GitSandbox CreateEmpty() => new(Path.Combine(ScratchRoot(), Guid.NewGuid().ToString("N")[..10]));

    public string PathOf(string relative) => Path.Combine(RepoPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    /// <summary>Runs git in the repository and returns its output; a failure fails the test.</summary>
    public async Task<string> GitAsync(params string[] args) => await GitInAsync(RepoPath, args);

    public async Task<string> GitInAsync(string folder, params string[] args)
    {
        var result = await GitProcess.RunAsync(Env, folder, args, null, TestContext.Current.CancellationToken);
        if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}{result.Output}");
        return result.Output;
    }

    /// <summary>Writes files, stages and commits them with a fixed time, returning the commit hash.</summary>
    public async Task<string> CommitAsync(string message, params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files) Write(path, content);
        await GitAsync("add", "-A");
        await GitAsync("commit", "-q", "-m", message);
        return (await GitAsync("rev-parse", "HEAD")).Trim();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Commits at fixed times, so history order and the graph are the same on every run.</summary>
internal static class GitSandboxHistory
{
    private static GitEnvironment At(GitEnvironment env, long unix, string author)
    {
        var vars = new Dictionary<string, string?>(env.Variables)
        {
            ["GIT_AUTHOR_DATE"] = $"{unix} +0000",
            ["GIT_COMMITTER_DATE"] = $"{unix} +0000",
            ["GIT_AUTHOR_NAME"] = author,
            ["GIT_AUTHOR_EMAIL"] = author == "Test" ? "test@example.invalid" : $"{author.ToLowerInvariant()}@example.invalid",
        };
        return new GitEnvironment { Timeout = env.Timeout, Variables = vars, Log = env.Log };
    }

    /// <summary>Writes the files and commits them at <paramref name="unix"/>; returns the hash.</summary>
    public static async Task<string> CommitAtAsync(this GitSandbox box, long unix, string message, string author = "Test", params (string Path, string Content)[] files)
    {
        foreach (var (path, content) in files) box.Write(path, content);
        var env = At(box.Env, unix, author);
        await Run(env, box.RepoPath, "add", "-A");
        await Run(env, box.RepoPath, "commit", "-q", "--allow-empty", "-m", message);
        return (await box.GitAsync("rev-parse", "HEAD")).Trim();
    }

    public static async Task MergeAtAsync(this GitSandbox box, long unix, string branch, string message)
    {
        await Run(At(box.Env, unix, "Test"), box.RepoPath, "merge", "-q", "--no-ff", branch, "-m", message);
    }

    private static async Task Run(GitEnvironment env, string folder, params string[] args)
    {
        var result = await GitProcess.RunAsync(env, folder, args, null, TestContext.Current.CancellationToken);
        if (!result.Ok) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Error}{result.Output}");
    }
}
