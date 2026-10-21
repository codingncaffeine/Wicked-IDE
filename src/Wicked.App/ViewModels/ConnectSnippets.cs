using System.Globalization;

namespace Wicked.App.ViewModels;

/// <summary>One "Connect …" recipe: the program it is for, what to do, and the text to copy.</summary>
public sealed record ConnectSnippet(string Program, string Where, string Text, string? Note = null);

/// <summary>
/// The settings other programs need to talk to Wicked's server (D12), written out for the ones people use: VS Code's Continue,
/// Zed, Cursor, Aider, Open WebUI and plain curl. The address, the model and the key are the real ones; a key that does not exist
/// yet is shown as <c>not-needed</c> (most clients insist on something there).
/// </summary>
public static class ConnectSnippets
{
    public static IReadOnlyList<ConnectSnippet> Build(int port, string? model, string? apiKey)
    {
        var baseUrl = string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}");
        var name = model ?? "your-model";
        var key = string.IsNullOrEmpty(apiKey) ? "not-needed" : apiKey;
        return
        [
            new("VS Code (Continue)", "Add to ~/.continue/config.yaml",
                $"""
                models:
                  - name: Wicked
                    provider: openai
                    model: {name}
                    apiBase: {baseUrl}/v1
                    apiKey: {key}
                """),
            new("Zed", "Add to settings.json (Zed: Open Settings)",
                $$"""
                {
                  "language_models": {
                    "openai_compatible": {
                      "Wicked": {
                        "api_url": "{{baseUrl}}/v1",
                        "available_models": [
                          { "name": "{{name}}", "max_tokens": 32768 }
                        ]
                      }
                    }
                  }
                }
                """, "Zed asks for the API key in its agent panel; type " + key + "."),
            new("Cursor", "Settings > Models > OpenAI API Key > Override OpenAI Base URL",
                $"""
                Base URL: {baseUrl}/v1
                API key:  {key}
                Model:    {name}
                """, "Cursor sends model requests through its own servers, so an address on this machine does not work there unless it is reachable from the internet."),
            new("Aider", "Run in a terminal",
                $"aider --model openai/{name} --openai-api-base {baseUrl}/v1 --openai-api-key {key}"),
            new("Open WebUI", "Run it with these variables (from Docker, use host.docker.internal for 127.0.0.1)",
                $"OPENAI_API_BASE_URL={baseUrl}/v1 OPENAI_API_KEY={key} open-webui serve"),
            new("curl (OpenAI)", "Run in a terminal",
                $$"""curl {{baseUrl}}/v1/chat/completions -H "Content-Type: application/json" -H "Authorization: Bearer {{key}}" -d '{"model": "{{name}}", "messages": [{"role": "user", "content": "Hello"}]}'"""),
            new("curl (Anthropic)", "Run in a terminal",
                $$"""curl {{baseUrl}}/v1/messages -H "Content-Type: application/json" -H "x-api-key: {{key}}" -H "anthropic-version: 2023-06-01" -d '{"model": "{{name}}", "max_tokens": 256, "messages": [{"role": "user", "content": "Hello"}]}'"""),
        ];
    }
}
