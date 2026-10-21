using System.Net;
using System.Text;
using Wicked.App.Shell;
using Wicked.App.Views;
using Wicked.Core;
using Wicked.Core.Models;

namespace Wicked.App.ViewModels;

/// <summary>
/// The Help commands, with VS Code's ids: Get Started, the keyboard shortcuts reference, documentation, release notes,
/// Report Issue, the licence, About and Check for Updates. Links go to the browser through the same opener the model
/// links use; nothing is sent anywhere by Wicked itself (Report Issue fills in GitHub's form for the user to send).
/// </summary>
public sealed partial class ShellViewModel
{
    private GetStartedViewModel? _getStarted;

    /// <summary>Runs a workbench command by its id, as the palette would; an unknown id does nothing.</summary>
    public Task RunCommandAsync(string id) => Commands.FirstOrDefault(c => c.Id == id)?.Run() ?? Task.CompletedTask;

    private IEnumerable<WorkbenchCommand> HelpCommands()
    {
        Task Open(string url)
        {
            Models.OpenExternal(url);
            return Task.CompletedTask;
        }

        return
        [
            new("workbench.action.openWalkthrough", "Help", "Get Started", null, () =>
            {
                ShowGetStarted();
                return Task.CompletedTask;
            }),
            new("wicked.showAssistant", "View", "Show Assistant", null, () =>
            {
                AssistantVisible = true;
                return Task.CompletedTask;
            }),
            new("workbench.action.keybindingsReference", "Help", "Keyboard Shortcuts Reference", "Ctrl+K Ctrl+R", OpenShortcutsReferenceAsync),
            new("workbench.action.openDocumentationUrl", "Help", "Documentation", null, () => Open(SystemInfo.Repository + "#readme")),
            new("update.showCurrentReleaseNotes", "Help", "Release Notes", null, () => Open($"{SystemInfo.Repository}/releases/tag/v{SystemInfo.ShortVersion}")),
            new("workbench.action.openIssueReporter", "Help", "Report Issue...", null, () => Open(IssueUrl())),
            new("workbench.action.openLicenseUrl", "Help", "View Licence", null, () => Open(SystemInfo.Repository + "/blob/main/LICENSE")),
            new("workbench.action.showAboutDialog", "Help", "About", null, ShowAboutAsync),
            new("update.checkForUpdates", "Help", "Check for Updates...", null, CheckForAppUpdateAsync),
        ];
    }

    /// <summary>Help: Get Started, as a tab in the editor area (brought forward when it is already open).</summary>
    public void ShowGetStarted()
    {
        _getStarted ??= new GetStartedViewModel(this);
        var model = _getStarted;
        Workbench.OpenView("Get Started", () => new GetStartedView { DataContext = model });
    }

    /// <summary>What a bug report or the About box says about this machine (GPU and engine from the model state).</summary>
    public string DescribeSystem()
    {
        var state = Models.Services.State;
        return SystemInfo.Describe(state.Hardware?.PrimaryGpu?.Name, state.Engine?.Id);
    }

    /// <summary>GitHub's new-issue form with the system lines filled in; the user writes the rest and sends it.</summary>
    internal string IssueUrl()
    {
        var body = $"**What happened**\n\n\n**What you expected**\n\n\n**Steps to make it happen**\n1. \n\n---\n```\n{DescribeSystem()}\n```\n";
        return $"{SystemInfo.Repository}/issues/new?body={Uri.EscapeDataString(body)}";
    }

    private async Task ShowAboutAsync()
    {
        var info = await Task.Run(DescribeSystem);
        var detail = $"{info}\n\nFree software under the GNU General Public License, version 3 or later.\n{SystemInfo.Repository}";
        if (Workbench.Dialogs is not { } dialogs) return;
        if (await dialogs.ConfirmAsync($"Wicked {SystemInfo.ShortVersion}", detail, "Copy") && Clipboard is { } copy) await copy(info);
    }

    /// <summary>
    /// Writes the keys in force now (keybindings.json applied) as a printable page and opens it in the browser, as VS Code's
    /// reference card does; written on a worker into the cache folder.
    /// </summary>
    private async Task OpenShortcutsReferenceAsync()
    {
        var rows = Commands.Select(c => (c.Category, c.Title, Keys: Keys.DisplayKeysFor(c.Id))).Where(r => r.Keys.Length > 0).ToList();
        var path = Path.Combine(Paths.Cache, "keyboard-shortcuts.html");
        await Task.Run(() =>
        {
            Directory.CreateDirectory(Paths.Cache);
            File.WriteAllText(path, ShortcutsPage(rows));
        });
        Models.OpenExternal(path);
    }

    internal static string ShortcutsPage(IEnumerable<(string Category, string Title, string Keys)> rows)
    {
        var html = new StringBuilder();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Wicked keyboard shortcuts</title><style>")
            .Append("body{font:13px/1.4 system-ui,sans-serif;margin:24px;color:#222}h1{font-size:20px;margin:0 0 4px}")
            .Append("p{color:#666;margin:0 0 16px}.cols{columns:3 260px;column-gap:28px}section{break-inside:avoid;margin:0 0 16px}")
            .Append("h2{font-size:13px;text-transform:uppercase;letter-spacing:.05em;color:#555;border-bottom:1px solid #ddd;padding-bottom:2px}")
            .Append("table{width:100%;border-collapse:collapse}td{padding:2px 0;vertical-align:top}td.k{text-align:right;white-space:nowrap;padding-left:12px}")
            .Append("kbd{font:12px ui-monospace,monospace;background:#f2f2f2;border:1px solid #ccc;border-radius:3px;padding:0 4px}")
            .Append("@media print{body{margin:0}}</style></head><body>")
            .Append("<h1>Wicked keyboard shortcuts</h1><p>The keys in force now, your keybindings.json included. Wicked ")
            .Append(WebUtility.HtmlEncode(SystemInfo.ShortVersion)).Append(".</p><div class=\"cols\">");
        foreach (var group in rows.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            html.Append("<section><h2>").Append(WebUtility.HtmlEncode(group.Key)).Append("</h2><table>");
            foreach (var (_, title, keys) in group.OrderBy(r => r.Title, StringComparer.Ordinal))
            {
                html.Append("<tr><td>").Append(WebUtility.HtmlEncode(title)).Append("</td><td class=\"k\">")
                    .Append(string.Join(" ", keys.Split(' ').Select(k => "<kbd>" + WebUtility.HtmlEncode(k) + "</kbd>")))
                    .Append("</td></tr>");
            }

            html.Append("</table></section>");
        }

        return html.Append("</div></body></html>").ToString();
    }

    /// <summary>Help: Check for Updates: asks GitHub now and says what this install has to do (AUR, apt, a new archive).</summary>
    private async Task CheckForAppUpdateAsync()
    {
        Notices.Post(new Notice("app-check", "Checking for a newer Wicked…", NoticeKind.Progress));
        try
        {
            var kind = SystemInfo.Install();
            var result = await Task.Run(() => Models.Services.Updates.CheckNowAsync());
            Notices.Clear("app-check");
            if (result.Skipped is { } why)
            {
                var text = why == SweepSkip.Offline ? "Offline mode is on, so Wicked did not ask for updates." : "Wicked could not ask for updates right now.";
                Notices.Post(new Notice("app-check", text, NoticeKind.Info));
            }
            else if (result.App is { } release)
            {
                Notices.Post(new Notice("app-update", $"Wicked {release.Version} is available. {SystemInfo.HowToUpdate(kind, release.Version.TrimStart('v'))}",
                    NoticeKind.Action, ShellRoutes.UrlPrefix + release.Url, Actions: [new("notes", "Open release notes"), new("dismiss", "Dismiss")]));
            }
            else
            {
                Notices.Post(new Notice("app-check", $"Wicked {SystemInfo.ShortVersion} is the newest version.", NoticeKind.Success));
            }
        }
        catch (Exception ex)
        {
            Notices.Clear("app-check");
            Log.Warn($"Check for updates: {ex.Message}");
            Notices.Post(new Notice("app-check", $"Could not check for updates: {ex.Message}", NoticeKind.Warning));
        }
    }
}
