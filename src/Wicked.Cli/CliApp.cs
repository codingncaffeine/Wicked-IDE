using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Wicked.Contracts;
using Wicked.Core;
using Wicked.Core.Server;

namespace Wicked.Cli;

/// <summary>What the command line leaves to its surroundings, so a test can stand in for them.</summary>
public sealed class CliHooks
{
    /// <summary>Runs <c>wickedd</c> in the foreground with the given arguments and returns its exit code (<c>serve</c>).</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<int>>? ServeInForeground { get; init; }

    /// <summary>Starts a daemon in the background (<c>pull</c>, <c>run</c> and the others need one); false when none can be started.</summary>
    public Func<CancellationToken, Task<bool>>? StartInBackground { get; init; }

    /// <summary>How long to wait for a daemon that was just started.</summary>
    public TimeSpan StartupWait { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// The <c>wicked-cli</c> commands: <c>models</c>, <c>ps</c>, <c>pull</c>, <c>run</c>, <c>unload</c>, <c>update</c>, <c>serve</c>, <c>status</c> and <c>paths</c>.
/// Everything but the last two is the daemon's business, asked over its socket (and started in the background when none runs).
/// </summary>
public sealed class CliApp(WickedPaths paths, TextWriter stdout, TextWriter stderr, TextReader stdin, CliHooks hooks)
{
    public const string Usage = """
        Usage: wicked-cli <command>

          models                     List the installed models and whether each is loaded.
          ps                         List the models that are loaded.
          pull <id>                  Download a model: a catalog id, or a Hugging Face repo (publisher/name), optionally as id:QUANT.
          run <id> [prompt]          Chat with a model in the terminal; give a prompt for a single answer.
          unload [id]                Unload a model, or every loaded model.
          update [--check]           Look for newer uploads of the models you have and list them.
          update --all               Update every model that has a newer upload.
          update <model>             Update one model.
          serve [--port N]           Run the daemon in the foreground, serving the OpenAI and Anthropic APIs.
          serve --stop               Ask the running daemon to stop.
          status                     Say whether the daemon is running, and which build.
          paths                      Print the folders Wicked uses for this profile.
          --version                  Print the version and licence, then exit.

        pull accepts --accept-licence to agree to a model's terms without being asked.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count == 0)
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            await stdout.WriteLineAsync(Usage).ConfigureAwait(false);
            return 0;
        }

        if (args.Any(a => a is "--version" or "-V"))
        {
            await stdout.WriteLineAsync($"wicked-cli {BuildInfo.Stamp}").ConfigureAwait(false);
            await stdout.WriteLineAsync(BuildInfo.Licence).ConfigureAwait(false);
            return 0;
        }

        var rest = args.Skip(1).ToList();
        try
        {
            return args[0] switch
            {
                "status" => await StatusAsync(ct).ConfigureAwait(false),
                "paths" => await PathsAsync().ConfigureAwait(false),
                "serve" => await ServeAsync(rest, ct).ConfigureAwait(false),
                "models" => await ModelsAsync(onlyLoaded: false, ct).ConfigureAwait(false),
                "ps" => await ModelsAsync(onlyLoaded: true, ct).ConfigureAwait(false),
                "unload" => await UnloadAsync(rest, ct).ConfigureAwait(false),
                "pull" => await PullAsync(rest, ct).ConfigureAwait(false),
                "run" => await RunModelAsync(rest, ct).ConfigureAwait(false),
                "update" => await UpdateAsync(rest, ct).ConfigureAwait(false),
                _ => await UnknownAsync(args[0]).ConfigureAwait(false),
            };
        }
        catch (CliException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return ex.Code;
        }
        catch (HttpRequestException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private sealed class CliException(string message, int code = 1) : Exception(message)
    {
        public int Code { get; } = code;
    }

    private async Task<int> UnknownAsync(string command)
    {
        await stderr.WriteLineAsync($"Unknown command: {command}. Try wicked-cli --help.").ConfigureAwait(false);
        return 2;
    }

    // ── status, paths ────────────────────────────────────────────────────────────────────────────

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        using var client = new DaemonClient(paths.DaemonSocket, TimeSpan.FromSeconds(3));
        var health = await client.GetHealthAsync(ct).ConfigureAwait(false);
        if (health is null)
        {
            await stdout.WriteLineAsync($"The daemon is not running (no answer on {paths.DaemonSocket}).").ConfigureAwait(false);
            return 1;
        }

        await stdout.WriteLineAsync($"The daemon is running: wickedd {health.Version}, process {health.ProcessId}, since {health.StartedAt:yyyy-MM-dd HH:mm:ss}.").ConfigureAwait(false);
        if (await client.GetStatusAsync(ct).ConfigureAwait(false) is { } status)
        {
            var gateway = status.Gateway;
            await stdout.WriteLineAsync(gateway.Running ? $"The API is served at {gateway.Address}/v1 ({(gateway.ListensOnLan ? "this machine and the network" : "this machine only")}; {status.KeyCount} API key{(status.KeyCount == 1 ? "" : "s")})."
                : gateway.Error ?? (gateway.Enabled ? "The API is not up yet." : "The API is switched off in settings.")).ConfigureAwait(false);
            await stdout.WriteLineAsync($"{status.Installed} model{(status.Installed == 1 ? "" : "s")} installed, {status.Loaded} loaded.").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> PathsAsync()
    {
        await stdout.WriteLineAsync($"config   {paths.Config}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"data     {paths.Data}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"state    {paths.State}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"cache    {paths.Cache}").ConfigureAwait(false);
        await stdout.WriteLineAsync($"runtime  {paths.Runtime}").ConfigureAwait(false);
        if (paths.Isolated) await stdout.WriteLineAsync($"(isolated by {WickedPaths.HomeVariable})").ConfigureAwait(false);
        return 0;
    }

    // ── serve ────────────────────────────────────────────────────────────────────────────────────

    private async Task<int> ServeAsync(List<string> args, CancellationToken ct)
    {
        if (args.Contains("--stop"))
        {
            using var client = new DaemonClient(paths.DaemonSocket, TimeSpan.FromSeconds(5));
            if (await client.GetHealthAsync(ct).ConfigureAwait(false) is null)
            {
                await stdout.WriteLineAsync("The daemon is not running.").ConfigureAwait(false);
                return 0;
            }

            using var response = await client.Http.PostAsync("v1/wicked/shutdown", null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            for (var i = 0; i < 100 && await client.GetHealthAsync(ct).ConfigureAwait(false) is not null; i++) await Task.Delay(100, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync("The daemon has stopped.").ConfigureAwait(false);
            return 0;
        }

        var port = "";
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Count && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 65535)
            {
                port = n.ToString(CultureInfo.InvariantCulture);
                i++;
            }
            else
            {
                throw new CliException($"Unknown option for serve: {args[i]}", 2);
            }
        }

        using (var probe = new DaemonClient(paths.DaemonSocket, TimeSpan.FromSeconds(3)))
        {
            if (await probe.GetStatusAsync(ct).ConfigureAwait(false) is { } running)
            {
                await stdout.WriteLineAsync(running.Gateway.Running ? $"The daemon is already running; the API is at {running.Gateway.Address}/v1." : "The daemon is already running.").ConfigureAwait(false);
                return 3;
            }
        }

        if (hooks.ServeInForeground is not { } serve) throw new CliException("The daemon (wickedd) was not found next to wicked-cli.");
        List<string> daemonArgs = ["--serve"];
        if (port.Length > 0) daemonArgs.AddRange(["--port", port]);
        return await serve(daemonArgs, ct).ConfigureAwait(false);
    }

    // ── what the daemon knows ────────────────────────────────────────────────────────────────────

    /// <summary>A client for the daemon, starting one in the background when none answers.</summary>
    private async Task<DaemonClient> DaemonAsync(CancellationToken ct, TimeSpan? timeout = null)
    {
        var client = new DaemonClient(paths.DaemonSocket, timeout ?? TimeSpan.FromSeconds(30));
        if (await client.GetHealthAsync(ct).ConfigureAwait(false) is not null) return client;
        if (hooks.StartInBackground is not { } start || !await start(ct).ConfigureAwait(false))
        {
            client.Dispose();
            throw new CliException("The daemon is not running, and wickedd could not be started. Run wicked-cli serve in another terminal.");
        }

        await stderr.WriteLineAsync("Started the daemon.").ConfigureAwait(false);
        var until = DateTime.UtcNow + hooks.StartupWait;
        while (DateTime.UtcNow < until)
        {
            if (await client.GetStatusAsync(ct).ConfigureAwait(false) is { Ready: true }) return client;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        client.Dispose();
        throw new CliException("The daemon started but did not become ready in time.");
    }

    private async Task<int> ModelsAsync(bool onlyLoaded, CancellationToken ct)
    {
        using var client = await DaemonAsync(ct).ConfigureAwait(false);
        var models = await client.GetAsync(onlyLoaded ? "v1/wicked/ps" : "v1/wicked/models", WickedJson.Default.ModelInfoArray, ct).ConfigureAwait(false) ?? [];
        if (models.Length == 0)
        {
            await stdout.WriteLineAsync(onlyLoaded ? "No model is loaded." : "No models are installed. Try: wicked-cli pull <id>").ConfigureAwait(false);
            return 0;
        }

        var rows = models.Select(m => new[] { m.Id, m.State, Size(m.SizeBytes), m.CatalogId ?? "", m.Context is { } c ? c.ToString(CultureInfo.InvariantCulture) : "" }).ToList();
        await WriteTableAsync(["ID", "STATE", "SIZE", "CATALOG ID", "CONTEXT"], rows).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UnloadAsync(List<string> args, CancellationToken ct)
    {
        using var client = await DaemonAsync(ct).ConfigureAwait(false);
        using var content = new StringContent(JsonSerializer.Serialize(new UnloadRequest(args.FirstOrDefault()), WickedJson.Default.UnloadRequest), Encoding.UTF8, "application/json");
        using var response = await client.Http.PostAsync("v1/wicked/unload", content, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new CliException($"{args.FirstOrDefault()} is not installed.");
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var unloaded = doc.RootElement.GetProperty("unloaded").EnumerateArray().Select(e => e.GetString()).ToList();
        await stdout.WriteLineAsync(unloaded.Count == 0 ? "Nothing was loaded." : "Unloaded " + string.Join(", ", unloaded)).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> PullAsync(List<string> args, CancellationToken ct)
    {
        var accept = args.Remove("--accept-licence") | args.Remove("--accept-license") | args.Remove("--yes");
        if (args.Count != 1) throw new CliException("Usage: wicked-cli pull <id>", 2);
        using var client = await DaemonAsync(ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        while (true)
        {
            var body = JsonSerializer.Serialize(new PullRequest(args[0], accept), WickedJson.Default.PullRequest);
            PullEvent? last = null;
            var lineOpen = false;
            await foreach (var data in client.StreamAsync("v1/wicked/pull", body, ct).ConfigureAwait(false))
            {
                var ev = JsonSerializer.Deserialize(data, WickedJson.Default.PullEvent)!;
                last = ev;
                if (ev.Phase is "download" or "engine")
                {
                    var percent = ev.Fraction is { } f ? $"{f * 100:0}%" : "";
                    await stdout.WriteAsync($"\r{ev.Message} {percent}".PadRight(78)).ConfigureAwait(false);
                    lineOpen = true;
                }
                else
                {
                    if (lineOpen) await stdout.WriteLineAsync().ConfigureAwait(false);
                    lineOpen = false;
                    if (ev.Phase != "licence") await (ev.Phase == "error" ? stderr : stdout).WriteLineAsync(ev.Message).ConfigureAwait(false);
                }
            }

            if (lineOpen) await stdout.WriteLineAsync().ConfigureAwait(false);
            if (last is { Phase: "licence" } licence)
            {
                await stdout.WriteLineAsync(licence.Message).ConfigureAwait(false);
                await stdout.WriteAsync("Accept and install? [y/N] ").ConfigureAwait(false);
                var answer = await stdin.ReadLineAsync(ct).ConfigureAwait(false);
                if (answer is not null && answer.Trim().StartsWith('y')) accept = true;
                else
                {
                    await stdout.WriteLineAsync("Not installed.").ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            return last is { Phase: "done" } ? 0 : 1;
        }
    }

    private async Task<int> RunModelAsync(List<string> args, CancellationToken ct)
    {
        if (args.Count == 0) throw new CliException("Usage: wicked-cli run <id> [prompt]", 2);
        var model = args[0];
        var oneShot = args.Count > 1 ? string.Join(' ', args.Skip(1)) : null;
        using var client = await DaemonAsync(ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        var loaded = await client.GetAsync("v1/wicked/ps", WickedJson.Default.ModelInfoArray, ct).ConfigureAwait(false) ?? [];
        if (!loaded.Any(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase) || string.Equals(m.CatalogId, model, StringComparison.OrdinalIgnoreCase)))
        {
            await stderr.WriteLineAsync($"Loading {model}…").ConfigureAwait(false);
        }

        var history = new List<(string Role, string Content)>();
        if (oneShot is not null) return await AskAsync(client, model, history, oneShot, ct).ConfigureAwait(false) is null ? 1 : 0;

        await stderr.WriteLineAsync("Send a message. /clear forgets the conversation, /bye (or Ctrl+D) ends it.").ConfigureAwait(false);
        while (true)
        {
            await stdout.WriteAsync(">>> ").ConfigureAwait(false);
            var line = await stdin.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null || line.Trim() is "/bye" or "/exit" or "/quit")
            {
                await stdout.WriteLineAsync().ConfigureAwait(false);
                return 0;
            }

            if (line.Trim() == "/clear")
            {
                history.Clear();
                await stdout.WriteLineAsync("The conversation is cleared.").ConfigureAwait(false);
                continue;
            }

            if (line.Trim().Length == 0) continue;
            await AskAsync(client, model, history, line, ct).ConfigureAwait(false);
            await stdout.WriteLineAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Sends one message with the conversation so far and prints the reply as it streams; returns the reply, or null when it failed.</summary>
    private async Task<string?> AskAsync(DaemonClient client, string model, List<(string Role, string Content)> history, string message, CancellationToken ct)
    {
        history.Add(("user", message));
        var body = JsonSerializer.Serialize(new
        {
            model,
            stream = true,
            messages = history.Select(h => new { role = h.Role, content = h.Content }),
        });
        var reply = new StringBuilder();
        try
        {
            await foreach (var data in client.StreamAsync("v1/chat/completions", body, ct).ConfigureAwait(false))
            {
                if (data == "[DONE]") break;
                using var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("error", out var error))
                {
                    await stderr.WriteLineAsync(error.TryGetProperty("message", out var m) ? m.GetString() : data).ConfigureAwait(false);
                    history.RemoveAt(history.Count - 1);
                    return null;
                }

                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } piece)
                {
                    reply.Append(piece);
                    await stdout.WriteAsync(piece).ConfigureAwait(false);
                    await stdout.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            await stderr.WriteLineAsync(ex.Message).ConfigureAwait(false);
            history.RemoveAt(history.Count - 1);
            return null;
        }

        history.Add(("assistant", reply.ToString()));
        return reply.ToString();
    }

    private async Task<int> UpdateAsync(List<string> args, CancellationToken ct)
    {
        using var client = await DaemonAsync(ct, Timeout.InfiniteTimeSpan).ConfigureAwait(false);
        if (args.Contains("--all") || args.Any(a => !a.StartsWith('-')))
        {
            var target = args.FirstOrDefault(a => !a.StartsWith('-'));
            var results = await client.PostAsync("v1/wicked/updates/apply", new UpdateRequest(target, All: target is null), WickedJson.Default.UpdateRequest, WickedJson.Default.UpdateResultInfoArray, ct).ConfigureAwait(false) ?? [];
            if (results.Length == 0)
            {
                await stdout.WriteLineAsync("Nothing to update.").ConfigureAwait(false);
                return 0;
            }

            foreach (var r in results) await stdout.WriteLineAsync($"{r.ModelId}: {r.Message}").ConfigureAwait(false);
            return results.Any(r => r.Result is "rolledBack" or "failed") ? 1 : 0;
        }

        using var response = await client.Http.PostAsync("v1/wicked/updates/check", null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var report = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), WickedJson.Default.UpdateReport)!;
        if (report.Skipped is { } skipped)
        {
            await stdout.WriteLineAsync(skipped switch { "Offline" => "Offline mode is on, so nothing was asked.", "NoNetwork" => "There is no network right now.", _ => "Nothing was asked: " + skipped + "." }).ConfigureAwait(false);
            return 0;
        }

        if (report.Models.Count == 0) await stdout.WriteLineAsync(report.Problem ?? "Every model is up to date.").ConfigureAwait(false);
        else
        {
            await WriteTableAsync(["MODEL", "WHAT", "FROM", "TO", "SIZE", "POLICY"], [.. report.Models.Select(u => new[] { u.ModelId, u.Detail, Short(u.From), Short(u.To), u.NewSize > 0 ? Size(u.NewSize) : "", u.Policy })]).ConfigureAwait(false);
            foreach (var u in report.Models.Where(u => u.Titles.Count > 0))
            {
                await stdout.WriteLineAsync().ConfigureAwait(false);
                await stdout.WriteLineAsync($"{u.ModelId}:").ConfigureAwait(false);
                foreach (var title in u.Titles) await stdout.WriteLineAsync($"  - {title}").ConfigureAwait(false);
            }

            await stdout.WriteLineAsync().ConfigureAwait(false);
            await stdout.WriteLineAsync("Update one with: wicked-cli update <model>, or all of them with: wicked-cli update --all").ConfigureAwait(false);
        }

        if (report.Engine is { } engine) await stdout.WriteLineAsync($"A newer engine is available ({engine}); update it in Settings > Runtime.").ConfigureAwait(false);
        if (report.App is { } app) await stdout.WriteLineAsync($"Wicked {app} is available.").ConfigureAwait(false);
        return 0;
    }

    // ── output ───────────────────────────────────────────────────────────────────────────────────

    private static string Short(string? revision) => string.IsNullOrEmpty(revision) ? "" : revision.Length > 7 ? revision[..7] : revision;

    private static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    private async Task WriteTableAsync(string[] header, List<string[]> rows)
    {
        var widths = header.Select((h, i) => Math.Max(h.Length, rows.Max(r => r[i].Length))).ToArray();
        string Line(string[] cells) => string.Join("  ", cells.Select((c, i) => c.PadRight(widths[i]))).TrimEnd();
        await stdout.WriteLineAsync(Line(header)).ConfigureAwait(false);
        foreach (var row in rows) await stdout.WriteLineAsync(Line(row)).ConfigureAwait(false);
    }

    /// <summary>The default surroundings: <c>wickedd</c> found beside this program, run in front or started behind.</summary>
    public static CliHooks DefaultHooks()
    {
        static string? Daemon() => DaemonLocator.Find();
        return new CliHooks
        {
            ServeInForeground = Daemon() is null ? null : async (daemonArgs, ct) =>
            {
                var info = new ProcessStartInfo(Daemon()!) { UseShellExecute = false };
                foreach (var a in daemonArgs) info.ArgumentList.Add(a);
                using var process = Process.Start(info) ?? throw new IOException("wickedd did not start");
                await using var _ = ct.Register(() =>
                {
                    try
                    {
                        process.Kill();
                    }
                    catch (InvalidOperationException)
                    {
                        // Already gone.
                    }
                });
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                return process.ExitCode;
            },
            StartInBackground = Daemon() is null ? null : _ =>
            {
                // Its own session, so closing this terminal does not take the daemon with it.
                var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : null;
                var info = new ProcessStartInfo(setsid ?? Daemon()!) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                if (setsid is not null) info.ArgumentList.Add(Daemon()!);
                try
                {
                    Process.Start(info)?.Dispose();
                    return Task.FromResult(true);
                }
                catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
                {
                    return Task.FromResult(false);
                }
            },
        };
    }
}
