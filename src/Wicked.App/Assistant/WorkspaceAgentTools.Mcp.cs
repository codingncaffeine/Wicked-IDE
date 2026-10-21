using System.Text.Json;
using System.Text.RegularExpressions;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Mcp;

namespace Wicked.App.Assistant;

/// <summary>
/// MCP servers' tools for the agent: the user's own servers (the profile's mcp.json) start with the first agent turn; a
/// project's (.vscode/mcp.json) runs a program from the repository, so it waits for the user's Allow first. Each tool is
/// offered as mcp_&lt;server&gt;_&lt;tool&gt;, and every call asks first (Always remembers that tool).
/// </summary>
public sealed partial class WorkspaceAgentTools
{
    private readonly Dictionary<string, McpClient> _mcp = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mcpRefused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _mcpAlways = new(StringComparer.Ordinal);
    private readonly Dictionary<string, McpTool> _mcpByName = new(StringComparer.Ordinal);

    /// <summary>The MCP servers the agent may still start this session (a project's, awaiting consent), for the next turn's step.</summary>
    private readonly List<McpServerConfig> _mcpWaiting = [];

    private IReadOnlyList<ChatTool> McpDefinitions => [.. _mcpByName.Select(kv => new ChatTool(kv.Key, $"[{kv.Value.Server}] {kv.Value.Description}", kv.Value.InputSchema))];

    private McpTool? McpToolFor(string name) => _mcpByName.GetValueOrDefault(name);

    /// <summary>A tool's name as OpenAI's API takes it: letters, digits, _ and -, at most 64 characters.</summary>
    internal static string ToolName(string server, string tool)
    {
        var name = Regex.Replace($"mcp_{server}_{tool}", "[^A-Za-z0-9_-]", "_");
        return name.Length <= 64 ? name : name[..64];
    }

    private async Task StartMcpAsync(CancellationToken ct)
    {
        var configs = new List<(McpServerConfig Config, bool FromProject)>();
        var own = Path.Combine(shell.Paths.Config, "mcp.json");
        if (File.Exists(own)) configs.AddRange(Read(own, null).Select(c => (c, false)));
        if (shell.Workbench.Root is { } root && Path.Combine(root, ".vscode", "mcp.json") is var project && File.Exists(project)) configs.AddRange(Read(project, root).Select(c => (c, true)));
        foreach (var (config, fromProject) in configs)
        {
            if (_mcp.ContainsKey(config.Name) || _mcpRefused.Contains(config.Name)) continue;
            if (fromProject)
            {
                if (!_mcpWaiting.Any(w => w.Name == config.Name)) _mcpWaiting.Add(config);
                continue;
            }

            await StartAsync(config, ct);
        }
    }

    private IEnumerable<McpServerConfig> Read(string path, string? cwd)
    {
        try
        {
            return McpConfig.Parse(File.ReadAllText(path), cwd);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            shell.Output.Write("MCP", $"{path} could not be read: {ex.Message}");
            return [];
        }
    }

    private async Task<bool> StartAsync(McpServerConfig config, CancellationToken ct)
    {
        try
        {
            var client = await McpClient.StartAsync(config, Wicked.Core.Http.WickedHttp.Shared, ct);
            client.Log += line => shell.Output.Write("MCP", $"[{config.Name}] {line}");
            _mcp[config.Name] = client;
            foreach (var tool in client.Tools) _mcpByName[ToolName(config.Name, tool.Name)] = tool;
            shell.Output.Write("MCP", $"Started {config.Name}: {client.Tools.Count} tool(s)");
            return true;
        }
        catch (Exception ex) when (ex is McpException or IOException or System.ComponentModel.Win32Exception or TimeoutException or HttpRequestException)
        {
            _mcpRefused.Add(config.Name);
            shell.Output.Write("MCP", $"{config.Name} did not start: {ex.Message}");
            Log.Warn($"MCP server {config.Name} did not start: {ex.Message}");
            return false;
        }
    }

    /// <summary>The project's servers that wait for consent, asked about as steps of the turn that needs them.</summary>
    internal async Task AskForProjectServersAsync(IAgentSteps steps, CancellationToken ct)
    {
        foreach (var config in _mcpWaiting.ToList())
        {
            _mcpWaiting.Remove(config);
            var what = config.Command is not null ? $"{config.Command} {string.Join(' ', config.Args)}" : config.Url;
            var step = await steps.AddAsync("Icon.Plug", $"Start MCP server {config.Name}");
            await steps.UpdateAsync(() => step.Detail = $"This project's .vscode/mcp.json runs: {what}");
            if (!await ConsentAsync(steps, step, $"Start {config.Name} from this project?", false, () => { }, ct))
            {
                _mcpRefused.Add(config.Name);
                continue;
            }

            var started = await StartAsync(config, ct);
            await steps.UpdateAsync(() =>
            {
                step.State = started ? StepState.Done : StepState.Failed;
                step.Text = started ? $"Started MCP server {config.Name}" : $"MCP server {config.Name} did not start";
            });
        }
    }

    private async Task<string> CallMcpAsync(IAgentSteps steps, McpTool tool, JsonElement args, CancellationToken ct)
    {
        var step = await steps.AddAsync("Icon.Plug", $"{tool.Server}: {tool.Name}");
        await steps.UpdateAsync(() => step.Detail = args.GetRawText());
        var key = $"{tool.Server}/{tool.Name}"; // not a path
        if (!await ConsentAsync(steps, step, $"Let {tool.Server} run {tool.Name}?", _mcpAlways.Contains(key), () => _mcpAlways.Add(key), ct)) return "The user did not allow this tool.";
        if (!_mcp.TryGetValue(tool.Server, out var client)) return $"{tool.Server} is not running.";
        try
        {
            var result = await client.CallAsync(tool.Name, args, ct);
            await steps.UpdateAsync(() =>
            {
                step.State = StepState.Done;
                step.Detail = result.Length > 2000 ? result[..2000] + "…" : result;
            });
            return result.Length > 16000 ? result[..16000] + "\n… (cut)" : result;
        }
        catch (McpException ex)
        {
            await steps.UpdateAsync(() =>
            {
                step.State = StepState.Failed;
                step.Detail = ex.Message;
            });
            return $"{tool.Name} failed: {ex.Message}";
        }
    }
}
