using Wicked.Core.Testing;

namespace Wicked.Core.Projects;

/// <summary>How making a project ended: its folder and the file to open, or what went wrong (the tool's last words).</summary>
public sealed record ProjectResult(string Root, string? OpenFile, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>
/// Makes a project from a template: the folder, each step in order (the language's own tool, or files), then git init and a
/// .gitignore when asked. Everything runs on the thread pool; every line a tool prints goes to <c>log</c>. A folder that
/// already holds something is refused, so nothing of the user's is overwritten.
/// </summary>
public static class ProjectCreator
{
    public static async Task<ProjectResult> CreateAsync(ProjectTemplate template, ProjectRequest request, ToolEnv env, Action<string> log, CancellationToken ct)
    {
        var root = request.Root;
        if (ProjectTemplates.CheckName(request.Name) is { } bad) return new ProjectResult(root, null, bad);
        if (!Directory.Exists(request.Location)) return new ProjectResult(root, null, $"{request.Location} does not exist");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) return new ProjectResult(root, null, $"{root} already exists and is not empty");
        if (File.Exists(root)) return new ProjectResult(root, null, $"{root} is a file");

        var steps = template.Steps(request);
        // A tool that makes the project folder itself (cargo new) wants it absent; everything else runs inside it.
        if (!steps.OfType<RunStep>().Any(s => s.InParent)) Directory.CreateDirectory(root);
        else if (Directory.Exists(root)) Directory.Delete(root);

        foreach (var step in steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (step)
            {
                case RunStep run:
                {
                    var where = run.InParent ? request.Location : root;
                    log($"> {run.Program} {string.Join(' ', run.Args)}");
                    var result = await ToolRunner.RunAsync(env, run.Program, run.Args, where, log, ct).ConfigureAwait(false);
                    if (!result.Started) return new ProjectResult(root, null, result.StartError);
                    if (result.ExitCode != 0)
                    {
                        var last = string.Join('\n', result.Lines.Where(l => l.Trim().Length > 0).TakeLast(4));
                        return new ProjectResult(root, null, $"{run.Program} {run.Args.FirstOrDefault()} failed (exit code {result.ExitCode})" + (last.Length > 0 ? ":\n" + last : ""));
                    }

                    break;
                }

                case WriteStep write:
                {
                    var path = Path.Combine(root, write.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, write.Text, ct).ConfigureAwait(false);
                    log($"wrote {write.Path}");
                    break;
                }
            }
        }

        if (!Directory.Exists(root)) return new ProjectResult(root, null, $"{template.Tool ?? "The template"} did not make {root}");
        if (request.Git) await InitGitAsync(template, root, env, log, ct).ConfigureAwait(false);
        var open = template.OpenFile(request) is { } relative && File.Exists(Path.Combine(root, relative)) ? Path.Combine(root, relative) : null;
        log($"Created {root}");
        return new ProjectResult(root, open, null);
    }

    /// <summary>git init and the template's .gitignore; a missing git only skips this (the project is still made).</summary>
    private static async Task InitGitAsync(ProjectTemplate template, string root, ToolEnv env, Action<string> log, CancellationToken ct)
    {
        if (template.GitIgnore.Length > 0 && !File.Exists(Path.Combine(root, ".gitignore")))
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), template.GitIgnore, ct).ConfigureAwait(false);
        }

        if (env.Find("git", root) is null)
        {
            log("git was not found: the project is not a repository yet");
            return;
        }

        log("> git init");
        var result = await ToolRunner.RunAsync(env, "git", ["init", "-q"], root, log, ct).ConfigureAwait(false);
        if (result.ExitCode != 0) log($"git init failed (exit code {result.ExitCode})");
    }
}
