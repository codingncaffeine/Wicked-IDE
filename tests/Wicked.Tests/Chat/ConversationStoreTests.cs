using Microsoft.Data.Sqlite;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

public sealed class ConversationStoreTests : IDisposable
{
    private readonly TempProfile _profile = new();
    private readonly ConversationStore _store;

    public ConversationStoreTests() => _store = new ConversationStore(Path.Combine(_profile.Paths.Data, "conversations.db"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _store.Dispose();
        _profile.Dispose();
    }

    private static Conversation Sample(string title, DateTimeOffset updated, Guid? id = null) =>
        new(id ?? Guid.NewGuid(), title, "a/b:Q4_K_M", "Be brief.",
        [
            new ChatMessage(ChatRole.User, "What is this?", [new ChatImage("image/png", [0, 1, 2, 255]), new ChatImage("image/jpeg", [9, 8])]),
            new ChatMessage(ChatRole.Assistant, "A cat.", Reasoning: "Whiskers, so a cat."),
            new ChatMessage(ChatRole.User, "Thanks é中\n\nline two"),
        ],
        updated.AddMinutes(-5), updated);

    [Fact]
    public async Task AConversationRoundTripsWithImagesReasoningAndOrder()
    {
        var saved = Sample("Cats", DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000));
        await _store.SaveAsync(saved, Ct);

        var loaded = await _store.GetAsync(saved.Id, Ct);

        Assert.NotNull(loaded);
        Assert.Equal(("Cats", "a/b:Q4_K_M", "Be brief."), (loaded.Title, loaded.Model, loaded.SystemPrompt));
        Assert.Equal(saved.Created, loaded.Created);
        Assert.Equal(saved.Updated, loaded.Updated);
        Assert.Equal([ChatRole.User, ChatRole.Assistant, ChatRole.User], loaded.Messages.Select(m => m.Role));
        Assert.Equal(saved.Messages.Select(m => m.Content), loaded.Messages.Select(m => m.Content));
        Assert.Equal("Whiskers, so a cat.", loaded.Messages[1].Reasoning);
        Assert.Null(loaded.Messages[2].Images);
        var images = loaded.Messages[0].Images!;
        Assert.Equal(["image/png", "image/jpeg"], images.Select(i => i.MediaType));
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, images[0].Data);
        Assert.Equal(new byte[] { 9, 8 }, images[1].Data);
    }

    [Fact]
    public async Task ListIsNewestFirstAndSavingAgainReplacesTheWholeConversation()
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var older = Sample("older", t);
        var newer = Sample("newer", t.AddHours(1));
        await _store.SaveAsync(older, Ct);
        await _store.SaveAsync(newer, Ct);
        Assert.Equal(["newer", "older"], (await _store.ListAsync(Ct)).Select(s => s.Title));

        await _store.SaveAsync(older with { Title = "older, edited", Updated = t.AddHours(2), Messages = [new ChatMessage(ChatRole.User, "only one")] }, Ct);

        var list = await _store.ListAsync(Ct);
        Assert.Equal(["older, edited", "newer"], list.Select(s => s.Title));
        Assert.Equal("a/b:Q4_K_M", list[0].Model);
        var loaded = await _store.GetAsync(older.Id, Ct);
        Assert.Equal("only one", Assert.Single(loaded!.Messages).Content);
    }

    [Fact]
    public async Task DeleteRemovesTheConversationItsMessagesAndItsImages()
    {
        var keep = Sample("keep", DateTimeOffset.UtcNow);
        var drop = Sample("drop", DateTimeOffset.UtcNow);
        await _store.SaveAsync(keep, Ct);
        await _store.SaveAsync(drop, Ct);

        await _store.DeleteAsync(drop.Id, Ct);
        await _store.DeleteAsync(Guid.NewGuid(), Ct); // unknown: nothing happens

        Assert.Null(await _store.GetAsync(drop.Id, Ct));
        Assert.Equal([keep.Id], (await _store.ListAsync(Ct)).Select(s => s.Id));
        Assert.Equal((0L, 0L), (await Count("messages", drop.Id), await Count("images", drop.Id)));
        Assert.Equal(2L, await Count("images", keep.Id));
    }

    [Fact]
    public async Task TheDatabaseIsInWalModeAndRecordsItsVersion()
    {
        await _store.SaveAsync(Sample("x", DateTimeOffset.UtcNow), Ct);
        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath};Pooling=False");
        await connection.OpenAsync(Ct);

        Assert.Equal("wal", await Scalar(connection, "PRAGMA journal_mode"));
        Assert.Equal((long)ConversationStore.CurrentVersion, await Scalar(connection, "SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public async Task AVersion1FileIsMigratedInPlaceAndKeepsItsConversations()
    {
        var path = Path.Combine(_profile.Root, "old.db");
        var id = Guid.NewGuid();
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await Run(connection, "CREATE TABLE schema_version (version INTEGER NOT NULL)");
            foreach (var statement in ConversationStore.Steps[0]) await Run(connection, statement);
            await Run(connection, "INSERT INTO schema_version VALUES (1)");
            await Run(connection, $"INSERT INTO conversations VALUES ('{id}', 'From v1', NULL, 1000, 2000)");
            await Run(connection, $"INSERT INTO messages VALUES ('{id}', 0, 1, 'hello', NULL)");
            await Run(connection, $"INSERT INTO messages VALUES ('{id}', 1, 2, 'hi', 'thinking')");
        }

        using var store = new ConversationStore(path);
        var loaded = await store.GetAsync(id, Ct);

        Assert.Equal("From v1", loaded!.Title);
        Assert.Null(loaded.SystemPrompt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(2000), loaded.Updated);
        Assert.Equal(["hello", "hi"], loaded.Messages.Select(m => m.Content));
        Assert.Equal("thinking", loaded.Messages[1].Reasoning);
        await store.SaveAsync(loaded with { SystemPrompt = "now with a system prompt", Messages = [new ChatMessage(ChatRole.User, "x", [new ChatImage("image/png", [1])])] }, Ct);
        Assert.Equal("now with a system prompt", (await store.GetAsync(id, Ct))!.SystemPrompt);
        await using var check = new SqliteConnection($"Data Source={path};Pooling=False");
        await check.OpenAsync(Ct);
        Assert.Equal((long)ConversationStore.CurrentVersion, await Scalar(check, "SELECT MAX(version) FROM schema_version"));
    }

    [Fact]
    public async Task AFileFromANewerBuildIsRefusedRatherThanDamaged()
    {
        var path = Path.Combine(_profile.Root, "future.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await connection.OpenAsync(Ct);
            await Run(connection, "CREATE TABLE schema_version (version INTEGER NOT NULL)");
            await Run(connection, "INSERT INTO schema_version VALUES (99)");
        }

        using var store = new ConversationStore(path);

        var ex = await Assert.ThrowsAsync<IOException>(() => store.ListAsync(Ct));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSavesAllLand()
    {
        var conversations = Enumerable.Range(0, 20).Select(i => Sample($"c{i}", DateTimeOffset.UtcNow.AddSeconds(i))).ToList();

        await Task.WhenAll(conversations.Select(c => _store.SaveAsync(c, Ct)));

        Assert.Equal(20, (await _store.ListAsync(Ct)).Count);
    }

    [Fact]
    public async Task ACancelledCallDoesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _store.ListAsync(cts.Token));
    }

    private async Task<long> Count(string table, Guid id)
    {
        await using var connection = new SqliteConnection($"Data Source={_store.DatabasePath};Pooling=False");
        await connection.OpenAsync(Ct);
        return (long)(await Scalar(connection, $"SELECT COUNT(*) FROM {table} WHERE conversation_id = '{id}'"))!;
    }

    private static async Task<object?> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(Ct);
    }

    private static async Task Run(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }
}
