using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class ShellProfileTests
{
    private static readonly string[] EtcShells = ["# a comment", "/bin/sh", "/bin/bash", "/usr/bin/bash", "/usr/bin/zsh", "/usr/bin/fish", "/usr/bin/nologin", "/usr/bin/git-shell", "", "not-a-path"];

    private static List<ShellProfile> Detect(string? shell = "/usr/bin/zsh", Func<string, bool>? exists = null) =>
        ShellProfiles.Detect(v => v == "SHELL" ? shell : null, exists ?? (_ => true), _ => EtcShells);

    [Fact]
    public void TheShellsFileAndTheUsersShellAreListedOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/etc/shells is the Unix list (WindowsHasItsOwnShells covers Windows)");
        Assert.Equal(["zsh", "sh", "bash", "fish"], Detect().Select(p => p.Name));
    }

    [Fact]
    public void ShellsThatCannotBeRunAreLeftOut()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/etc/shells is the Unix list (WindowsHasItsOwnShells covers Windows)");
        Assert.DoesNotContain(Detect(), p => p.Name is "nologin" or "git-shell");
        Assert.Equal(["sh"], Detect(exists: p => p == "/bin/sh").Select(p => p.Name));
    }

    [Fact]
    public void EachShellKnowsWhatItIs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/etc/shells is the Unix list (WindowsHasItsOwnShells covers Windows)");
        var kinds = Detect().ToDictionary(p => p.Name, p => p.Kind);
        Assert.Equal(ShellKind.Zsh, kinds["zsh"]);
        Assert.Equal(ShellKind.Bash, kinds["bash"]);
        Assert.Equal(ShellKind.Fish, kinds["fish"]);
        Assert.Equal(ShellKind.Other, kinds["sh"]);
    }

    [Fact]
    public void TheSettingWinsThenTheUsersShell()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "/etc/shells is the Unix list (WindowsHasItsOwnShells covers Windows)");
        var profiles = Detect();
        Assert.Equal("fish", ShellProfiles.Choose(profiles, "fish", _ => "/usr/bin/zsh")!.Name);
        Assert.Equal("bash", ShellProfiles.Choose(profiles, "/bin/bash", _ => "/usr/bin/zsh")!.Name);
        Assert.Equal("zsh", ShellProfiles.Choose(profiles, "", v => v == "SHELL" ? "/usr/bin/zsh" : null)!.Name);
        Assert.Equal("zsh", ShellProfiles.Choose(profiles, "no-such-shell", v => v == "SHELL" ? "/usr/bin/zsh" : null)!.Name);
        Assert.Equal("sh", ShellProfiles.Choose(Detect(shell: null).Where(p => p.Name != "zsh").ToList(), "", _ => null)!.Name);
    }

    [Fact]
    public void WindowsHasItsOwnShells()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the Windows list");
        var root = @"C:\Windows";
        var env = new Dictionary<string, string?> { ["PATH"] = @"C:\Program Files\PowerShell\7", ["SystemRoot"] = root, ["ComSpec"] = root + @"\System32\cmd.exe", ["ProgramFiles"] = @"C:\Program Files" };
        var profiles = ShellProfiles.Detect(v => env.GetValueOrDefault(v), p => !p.EndsWith("wsl.exe", StringComparison.OrdinalIgnoreCase), _ => EtcShells);
        Assert.Equal(["PowerShell", "Windows PowerShell", "Command Prompt", "Git Bash"], profiles.Select(p => p.Name));
        Assert.Equal([ShellKind.PowerShell, ShellKind.PowerShell, ShellKind.Cmd, ShellKind.Bash], profiles.Select(p => p.Kind));
        Assert.Equal("PowerShell", ShellProfiles.Choose(profiles, "", _ => null)!.Name);
        Assert.Equal("Git Bash", ShellProfiles.Choose(profiles, "Git Bash", _ => null)!.Name);
    }

    [Fact]
    public void NoShellsAtAllIsNull() => Assert.Null(ShellProfiles.Choose([], "bash"));

    [Theory]
    [InlineData("bash", ShellKind.Bash)]
    [InlineData("zsh", ShellKind.Zsh)]
    [InlineData("fish", ShellKind.Fish)]
    [InlineData("pwsh.exe", ShellKind.PowerShell)]
    [InlineData("cmd.exe", ShellKind.Cmd)]
    [InlineData("dash", ShellKind.Other)]
    public void TheKindComesFromTheName(string name, ShellKind kind) => Assert.Equal(kind, ShellProfiles.KindOf(name));
}

public sealed class ShellIntegrationTests
{
    [Fact]
    public void BashStartsFromOurRcFile()
    {
        var (file, args, env) = ShellIntegration.Prepare(new ShellProfile("bash", "/bin/bash", [], ShellKind.Bash), "/integration", enabled: true);
        Assert.Equal("/bin/bash", file);
        Assert.Equal("--rcfile", args[0]);
        Assert.EndsWith("wicked.bash", args[1]);
        Assert.Equal("1", env["WICKED_SHELL_INTEGRATION"]);
        Assert.False(env.ContainsKey("WICKED_SHELL_LOGIN"));
    }

    [Fact]
    public void ALoginBashIsStartedWithoutTheLoginFlagAndToldToReadTheLoginFiles()
    {
        var (_, args, env) = ShellIntegration.Prepare(new ShellProfile("bash", "/bin/bash", ["--login"], ShellKind.Bash), "/integration", enabled: true);
        Assert.DoesNotContain("--login", args);
        Assert.Equal("1", env["WICKED_SHELL_LOGIN"]);
    }

    [Fact]
    public void ZshIsPointedAtOurConfigurationDirectoryAndRemembersTheUsers()
    {
        var (_, _, env) = ShellIntegration.Prepare(new ShellProfile("zsh", "/bin/zsh", [], ShellKind.Zsh), "/integration", enabled: true, v => v == "HOME" ? "/home/me" : null);
        Assert.EndsWith("zsh", env["ZDOTDIR"]);
        Assert.Equal("/home/me", env["WICKED_USER_ZDOTDIR"]);
        var (_, _, env2) = ShellIntegration.Prepare(new ShellProfile("zsh", "/bin/zsh", [], ShellKind.Zsh), "/integration", enabled: true, v => v == "ZDOTDIR" ? "/conf/zsh" : "/home/me");
        Assert.Equal("/conf/zsh", env2["WICKED_USER_ZDOTDIR"]);
    }

    [Fact]
    public void FishAndPowerShellGetAStartupCommand()
    {
        var (_, fish, _) = ShellIntegration.Prepare(new ShellProfile("fish", "/usr/bin/fish", [], ShellKind.Fish), "/integration", enabled: true);
        Assert.Equal("--init-command", fish[0]);
        Assert.Contains("wicked.fish", fish[1]);
        var (_, pwsh, _) = ShellIntegration.Prepare(new ShellProfile("pwsh", "pwsh", ["-NoLogo"], ShellKind.PowerShell), "/integration", enabled: true);
        Assert.Contains("-NoExit", pwsh);
        Assert.Contains("-Command", pwsh);
    }

    [Fact]
    public void DisabledOrUnknownShellsStartAsTheyAre()
    {
        var (_, args, env) = ShellIntegration.Prepare(new ShellProfile("bash", "/bin/bash", [], ShellKind.Bash), "/integration", enabled: false);
        Assert.Empty(args);
        Assert.Empty(env);
        var (_, dashArgs, dashEnv) = ShellIntegration.Prepare(new ShellProfile("dash", "/bin/dash", [], ShellKind.Other), "/integration", enabled: true);
        Assert.Empty(dashArgs);
        Assert.Empty(dashEnv);
    }

    [Fact]
    public void TheScriptsAreWrittenOutAndKeptCurrent()
    {
        using var dir = new ScratchDir();
        ShellIntegration.Install(dir.Root);
        foreach (var file in new[] { "bash/wicked.bash", "zsh/.zshrc", "zsh/.zshenv", "fish/wicked.fish", "pwsh/wicked.ps1" })
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, file)), file);
        }

        var path = Path.Combine(dir.Root, "bash", "wicked.bash");
        File.WriteAllText(path, "stale");
        ShellIntegration.Install(dir.Root);
        Assert.Contains("OSC 133", File.ReadAllText(path));
    }
}

/// <summary>A real bash with the integration, in a terminal session: the marks come out of the shell itself.</summary>
public sealed class BashIntegrationTests
{
    private static string Bash => new[] { "/bin/bash", "/usr/bin/bash" }.FirstOrDefault(File.Exists) ?? "";

    private static TerminalSession StartBash(ScratchDir home, string rc, out SessionWatcher watcher)
    {
        File.WriteAllText(Path.Combine(home.Root, ".bashrc"), rc);
        var integration = Path.Combine(home.Root, "integration");
        ShellIntegration.Install(integration);
        var profile = new ShellProfile("bash", Bash, [], ShellKind.Bash);
        var launch = ShellIntegration.Launch(profile, integration, integration: true, home.Root, 80, 24, new Dictionary<string, string?>
        {
            ["HOME"] = home.Root,
            ["HISTFILE"] = Path.Combine(home.Root, "history"),
            ["DBUS_SESSION_BUS_ADDRESS"] = null,
            ["WAYLAND_DISPLAY"] = null,
            ["DISPLAY"] = null,
            ["INPUTRC"] = "/dev/null",
        });
        var session = new TerminalSession(new TerminalSessionOptions { Launch = launch });
        watcher = new SessionWatcher(session);
        session.Start();
        return session;
    }

    [Fact]
    public async Task EachCommandIsMarkedWithItsTextAndItsExitCode()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Bash.Length == 0, "needs bash");
        using var home = new ScratchDir();
        using var session = StartBash(home, "PS1='my> '\n", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("my>", StringComparison.Ordinal)), watch.Screen);

        session.Input("echo hello\r");
        Assert.True(await watch.WaitForAsync(async () => (await session.GetCommandsAsync()).Any(c => c.Record.Finished)), watch.Screen);
        session.Input("false\r");
        Assert.True(await watch.WaitForAsync(async () => (await session.GetCommandsAsync()).Count(c => c.Record.Finished) == 2), watch.Screen);

        var commands = await session.GetCommandsAsync();
        Assert.Equal(["echo hello", "false"], commands.Where(c => c.Record.Finished).Select(c => c.Record.CommandLine));
        Assert.Equal([0, 1], commands.Where(c => c.Record.Finished).Select(c => c.Record.ExitCode!.Value));
        // The marks themselves are invisible: the prompt reads as the user wrote it.
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("my> echo hello\nhello", StringComparison.Ordinal)), watch.Screen);
    }

    [Fact]
    public async Task TheUsersPromptCommandAndPromptRunAndTheWorkingDirectoryIsReported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Bash.Length == 0, "needs bash");
        using var home = new ScratchDir();
        var marker = Path.Combine(home.Root, "prompt-ran");
        using var session = StartBash(home, $"PS1='\\u@h> '\nPROMPT_COMMAND='echo ran >> \"{marker}\"; PS1=\"[${{PWD##*/}}]> \"'\n", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("]>", StringComparison.Ordinal)), watch.Screen);
        Assert.True(File.Exists(marker));

        Directory.CreateDirectory(Path.Combine(home.Root, "sub dir"));
        session.Input("cd 'sub dir'\r");
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("[sub dir]>", StringComparison.Ordinal)), watch.Screen);
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(watch.Directories.Contains(Path.Combine(home.Root, "sub dir")))), string.Join("|", watch.Directories));
    }

    [Fact]
    public async Task ABlankPromptLeavesNoMarkAndTheExitEndsTheSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || Bash.Length == 0, "needs bash");
        using var home = new ScratchDir();
        using var session = StartBash(home, "PS1='$ '\n", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("$", StringComparison.Ordinal)));
        session.Input("\r\r");
        await Task.Delay(300, TestContext.Current.CancellationToken);
        session.Input("exit 7\r");
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(session.ExitCode is not null)), watch.Screen);
        Assert.Equal(7, session.ExitCode);
    }
}
