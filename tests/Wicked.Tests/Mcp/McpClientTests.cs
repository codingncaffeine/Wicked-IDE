using System.Text.Json;
using Wicked.Core.Mcp;

namespace Wicked.Tests.Mcp;

/// <summary>The MCP client against a stand-in stdio server (a bash script): initialize, list the tools page by page, call one.</summary>
public sealed class McpClientTests : IDisposable
{
    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Answers by method; the tool list comes in two pages; "echo" says back its text, "fail" reports an error result.
    private const string Server = """
        #!/bin/bash
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\([0-9]*\).*/\1/p')
          case "$line" in
            *'"method":"initialize"'*)
              echo "{\"jsonrpc\":\"2.0\",\"id\":$id,\"result\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{\"tools\":{}},\"serverInfo\":{\"name\":\"stand-in\",\"version\":\"1\"}}}" ;;
            *'"method":"notifications/initialized"'*)
              echo "initialized" >&2 ;;
            *'"method":"tools/list"'*'"cursor":"p2"'*)
              echo "{\"jsonrpc\":\"2.0\",\"id\":$id,\"result\":{\"tools\":[{\"name\":\"fail\",\"description\":\"Always fails\",\"inputSchema\":{\"type\":\"object\"}}]}}" ;;
            *'"method":"tools/list"'*)
              echo "{\"jsonrpc\":\"2.0\",\"id\":$id,\"result\":{\"tools\":[{\"name\":\"echo\",\"description\":\"Says it back\",\"inputSchema\":{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}}],\"nextCursor\":\"p2\"}}" ;;
            *'"name":"echo"'*)
              text=$(printf '%s' "$line" | sed -n 's/.*"text":"\([^"]*\)".*/\1/p')
              echo "{\"jsonrpc\":\"2.0\",\"id\":$id,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"echo: $text\"}]}}" ;;
            *'"name":"fail"'*)
              echo "{\"jsonrpc\":\"2.0\",\"id\":$id,\"result\":{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"it broke\"}]}}" ;;
          esac
        done
        """;

    private string WriteServer()
    {
        var path = Path.Combine(_profile.Root, "server.sh");
        File.WriteAllText(path, Server.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public async Task AStdioServerStartsListsEveryPageOfToolsAndAnswersCalls()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in server is a bash script");
        var config = new McpServerConfig("stand-in", WriteServer(), [], new Dictionary<string, string>(), null, new Dictionary<string, string>());
        var logged = new List<string>();
        await using var client = await McpClient.StartAsync(config, new HttpClient(), Ct);
        client.Log += line =>
        {
            lock (logged) logged.Add(line);
        };
        Assert.Equal(["echo", "fail"], client.Tools.Select(t => t.Name));
        Assert.Equal("Says it back", client.Tools[0].Description);
        Assert.Equal("echo: hello", await client.CallAsync("echo", JsonDocument.Parse("""{"text":"hello"}""").RootElement, Ct));
        var failed = await Assert.ThrowsAsync<McpException>(() => client.CallAsync("fail", default, Ct));
        Assert.Equal("it broke", failed.Message);
    }

    [Fact]
    public void McpJsonIsReadInVsCodesShapeAndClaudesToo()
    {
        var servers = McpConfig.Parse("""
            {
              // comments and trailing commas, as VS Code writes it
              "servers": {
                "files": { "type": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."], "env": { "A": "1" } },
                "remote": { "type": "http", "url": "https://example.invalid/mcp", "headers": { "Authorization": "Bearer x" } },
                "off": { "command": "x", "disabled": true },
              },
            }
            """, "/project");
        Assert.Equal(["files", "remote"], servers.Select(s => s.Name));
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "."], servers[0].Args);
        Assert.Equal("1", servers[0].Env["A"]);
        Assert.Equal("/project", servers[0].Cwd);
        Assert.Equal("https://example.invalid/mcp", servers[1].Url);
        Assert.Equal("Bearer x", servers[1].Headers["Authorization"]);
        Assert.Single(McpConfig.Parse("""{"mcpServers":{"a":{"command":"b"}}}"""));
    }

    [Theory]
    [InlineData("files", "read_file", "mcp_files_read_file")]
    [InlineData("my server", "do.it", "mcp_my_server_do_it")]
    public void ToolNamesAreWhatTheApiTakes(string server, string tool, string name) => Assert.Equal(name, Wicked.App.Assistant.WorkspaceAgentTools.ToolName(server, tool));
}
