using System.Text;
using System.Text.RegularExpressions;
using Wicked.App.ViewModels;

namespace Wicked.App.Assistant;

/// <summary>
/// The context a chat message brings (D18): @file:path, @folder:path, @problems, @git and @selection in its text, and the
/// open file when its chip is on, put before the question as tagged blocks the model reads. Files are read from their
/// editors when open (unsaved edits included), from disk on a worker otherwise, each cut to a size a local model's
/// context can take.
/// </summary>
public static partial class MentionContext
{
    private const int FileCap = 20_000;

    [GeneratedRegex(@"@(?:(file|folder):(\S+)|(problems|git|selection)\b)")]
    private static partial Regex Mention();

    public static async Task<string> ExpandAsync(ShellViewModel shell, string text, bool activeFile)
    {
        var blocks = new StringBuilder();
        var root = shell.Workbench.Root;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Mention().Matches(text))
        {
            var kind = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Value;
            if (!seen.Add(m.Value)) continue;
            switch (kind)
            {
                case "file" when Resolve(root, m.Groups[2].Value) is { } path:
                    await AppendFileAsync(shell, blocks, path, root);
                    break;
                case "folder" when shell.Workbench.Index is { } index:
                    var folder = m.Groups[2].Value.Trim('/', '.');
                    var files = index.Files.Where(f => folder.Length == 0 || f.StartsWith(folder + "/", StringComparison.Ordinal)).Take(300).ToList(); // not a path
                    blocks.Append("<folder path=\"").Append(folder).Append("\">\n").AppendJoin('\n', files).Append("\n</folder>\n\n");
                    break;
                case "problems":
                    var problems = shell.Language.Problems.Files.SelectMany(f => f.Items).Take(100)
                        .Select(p => $"{(root is null ? p.Path : Path.GetRelativePath(root, p.Path))}:{p.Line + 1}:{p.Column + 1}: {p.Severity.ToString().ToLowerInvariant()}: {p.Message}");
                    blocks.Append("<problems>\n").AppendJoin('\n', problems).Append("\n</problems>\n\n");
                    break;
                case "git" when root is not null && shell.SourceControl.RepositoryOf(root) is { } repo:
                    var staged = await repo.DiffAsync(".", staged: true);
                    var working = await repo.DiffAsync(".", staged: false);
                    var diff = (staged + working).Trim();
                    blocks.Append("<git-diff>\n").Append(diff.Length > FileCap ? diff[..FileCap] + "\n… (cut)" : diff).Append("\n</git-diff>\n\n");
                    break;
                case "selection" when shell.Workbench.ActiveTab is { } tab && !tab.Document.Primary.IsEmpty:
                    var p = tab.Document.Primary;
                    blocks.Append("<selection file=\"").Append(tab.Title).Append("\">\n").Append(tab.Document.Text.Substring(p.Start, p.Length)).Append("\n</selection>\n\n");
                    break;
            }
        }

        if (activeFile && shell.Workbench.ActiveTab is { ViewFactory: null, Path: { } open } active && seen.All(s => !s.EndsWith(Path.GetFileName(open), StringComparison.Ordinal)))
        {
            await AppendFileAsync(shell, blocks, open, root);
            var (line, _) = active.Document.Text.Position(active.Document.Primary.Active);
            blocks.Append("The user's cursor is on line ").Append(line + 1).Append(" of ").Append(active.Title).Append(".\n\n");
        }

        return blocks.Length == 0 ? text : $"Context:\n\n{blocks}{text}";
    }

    private static string? Resolve(string? root, string relative)
    {
        if (root is null) return null;
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Path.GetFullPath(root), StringComparison.Ordinal) ? full : null;
    }

    private static async Task AppendFileAsync(ShellViewModel shell, StringBuilder blocks, string path, string? root)
    {
        var open = shell.Workbench.AllTabs.FirstOrDefault(t => t.Path == path)?.Document.Text;
        string? text = null;
        if (open is not null) text = await Task.Run(() => open.Length > FileCap ? open.Substring(0, FileCap) : open.ToString());
        else if (File.Exists(path)) text = await Task.Run(async () =>
        {
            var loaded = await Wicked.Core.Files.TextFile.ReadAsync(path, false, CancellationToken.None);
            return loaded.Kind == Wicked.Core.Files.FileKind.Text ? loaded.Text : null;
        });
        if (text is null) return;
        if (text.Length > FileCap) text = text[..FileCap] + "\n… (cut)";
        var name = root is null ? Path.GetFileName(path) : Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        blocks.Append("<file path=\"").Append(name).Append("\">\n").Append(text).Append("\n</file>\n\n");
    }
}
