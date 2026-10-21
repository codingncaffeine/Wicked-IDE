using Wicked.App.Editor;
using Wicked.App.Shell;

namespace Wicked.Tests.Basics;

/// <summary>
/// The basics contract: every row of basics.tsv (the commands an IDE user expects, gathered from what VS Code, JetBrains,
/// Zed and Visual Studio publish) is still a command here, with the default keys the row names. A basic cannot quietly
/// disappear or move to other keys.
/// </summary>
public sealed class BasicsContractTests
{
    private static IEnumerable<(string Id, string Surface, string Keys)> Rows() =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Basics", "basics.tsv"))
            .Where(l => l.Length > 0 && l[0] != '#')
            .Select(l => l.Split('\t'))
            .Select(p => (p[0], p[1], p.Length > 2 ? p[2] : ""));

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task EveryBasicIsACommandWithItsKeys()
    {
        var rows = Rows().ToList();
        Assert.True(rows.Count > 50, $"only {rows.Count} rows read");
        using var profile = new TempProfile();
        var problems = await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var found = new List<string>();
            foreach (var (id, surface, keys) in rows)
            {
                if (surface == "workbench")
                {
                    if (!shell.Commands.Any(c => c.Id == id)) found.Add($"{id}: no such command");
                    else if (!OperatingSystem.IsWindows() && shell.Keys.DisplayKeysFor(id) != keys) found.Add($"{id}: keys '{shell.Keys.DisplayKeysFor(id)}', expected '{keys}'");
                }
                else if (surface == "editor")
                {
                    var gestures = EditorKeymap.Defaults.Where(d => d.Command.Id == id).Select(d => KeyNames.Readable(d.Gesture.ToString())).ToList();
                    if (gestures.Count == 0) found.Add($"{id}: no such editor command");
                    else if (!OperatingSystem.IsWindows() && keys.Length > 0 && !gestures.Contains(keys)) found.Add($"{id}: keys [{string.Join(", ", gestures)}], expected '{keys}'");
                }
                else
                {
                    found.Add($"{id}: unknown surface '{surface}'");
                }
            }

            window.Close();
            return found;
        });
        Assert.True(problems.Count == 0, "Basics missing or moved:\n" + string.Join('\n', problems));
    }
}
