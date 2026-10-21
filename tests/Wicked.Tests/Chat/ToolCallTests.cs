using System.Text.Json;
using System.Text.Json.Nodes;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

/// <summary>Tools in the chat: offered in the request, called in the stream, answered as tool messages.</summary>
public sealed class ToolCallTests
{
    [Fact]
    public void StreamedToolCallsComeOutPieceByPieceWithTheirIndexIdAndName()
    {
        var first = ChatEngine.Parse("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"read_file","arguments":""}}]}}]}""", null);
        var second = ChatEngine.Parse("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""", null);
        var third = ChatEngine.Parse("""{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a.cs\"}"}},{"index":1,"id":"call_2","function":{"name":"list_files","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}""", null);
        Assert.Equal(new ToolCallDelta(0, "call_1", "read_file", ""), first.Tools!.Single());
        Assert.Equal(new ToolCallDelta(0, null, null, "{\"path\":"), second.Tools!.Single());
        Assert.Equal([new ToolCallDelta(0, null, null, "\"a.cs\"}"), new ToolCallDelta(1, "call_2", "list_files", "{}")], third.Tools!);
        Assert.Equal("tool_calls", third.FinishReason);
        Assert.Null(first.Delta);
    }

    [Fact]
    public void TheRequestOffersTheToolsAndCarriesTheCallsAndTheirAnswers()
    {
        var schema = JsonDocument.Parse("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""").RootElement;
        var request = new ChatRequest("m", [
            new ChatMessage(ChatRole.User, "what is in a.cs?"),
            new ChatMessage(ChatRole.Assistant, "", ToolCalls: [new ChatToolCall("call_1", "read_file", """{"path":"a.cs"}""")]),
            new ChatMessage(ChatRole.Tool, "class A {}", ToolCallId: "call_1"),
        ], Tools: [new ChatTool("read_file", "Reads a file", schema)]);
        var body = JsonNode.Parse(ChatEngine.BuildBody(request))!;
        Assert.Equal("read_file", (string)body["tools"]![0]!["function"]!["name"]!);
        Assert.Equal("string", (string)body["tools"]![0]!["function"]!["parameters"]!["properties"]!["path"]!["type"]!);
        Assert.Equal("auto", (string)body["tool_choice"]!);
        var assistant = body["messages"]![1]!;
        Assert.Null(assistant["content"]);
        Assert.Equal("call_1", (string)assistant["tool_calls"]![0]!["id"]!);
        Assert.Equal("""{"path":"a.cs"}""", (string)assistant["tool_calls"]![0]!["function"]!["arguments"]!);
        var tool = body["messages"]![2]!;
        Assert.Equal("tool", (string)tool["role"]!);
        Assert.Equal("call_1", (string)tool["tool_call_id"]!);
        Assert.Equal("class A {}", (string)tool["content"]!);
    }
}
