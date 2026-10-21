namespace Wicked.App.Shell;

/// <summary>The places a notice or a command can send the user to.</summary>
public static class ShellRoutes
{
    public const string Explorer = "panel/explorer";
    public const string Search = "panel/search";
    public const string SourceControl = "panel/source-control";
    public const string Models = "panel/models";
    public const string Outline = "panel/outline";
    public const string Testing = "panel/testing";
    public const string Debug = "panel/debug";
    public const string DebugConsole = "panel/debug-console";
    public const string Extensions = "panel/extensions";
    public const string Assistant = "assistant";
    public const string SettingsModels = "settings/models";
    public const string SettingsRuntime = "settings/runtime";
    public const string SettingsServer = "settings/server";
    public const string Notifications = "notifications";
    public const string WhatsNew = "whats-new";

    /// <summary>A web address to open in the browser: <c>url/https://…</c>.</summary>
    public const string UrlPrefix = "url/";
}
