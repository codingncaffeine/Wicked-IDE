using Microsoft.Data.Sqlite;

namespace Wicked.Core.Chat;

/// <summary>
/// Saved conversations in one SQLite file (WAL mode) in the profile's data folder. A conversation is a row, its messages are rows
/// in order, and attached images are blobs in their own table. The schema is versioned in <c>schema_version</c> and upgraded
/// step by step on open. Every call runs on a worker; writes are serialised so a save is never seen half done.
/// </summary>
public sealed class ConversationStore : IConversationStore, IDisposable
{
    /// <summary>The schema this build writes. Older files are migrated in one transaction per step.</summary>
    public const int CurrentVersion = 2;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _ready;

    public ConversationStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = true }.ToString();
    }

    public string DatabasePath { get; }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default) =>
        OnWorkerAsync<IReadOnlyList<ConversationSummary>>(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, title, model, updated FROM conversations ORDER BY updated DESC, created DESC, id";
            using var reader = command.ExecuteReader();
            var list = new List<ConversationSummary>();
            while (reader.Read())
            {
                list.Add(new ConversationSummary(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), FromMillis(reader.GetInt64(3))));
            }

            return list;
        }, ct);

    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default) =>
        OnWorkerAsync<Conversation?>(connection =>
        {
            using var head = connection.CreateCommand();
            head.CommandText = "SELECT title, model, system_prompt, created, updated FROM conversations WHERE id = $id";
            head.Parameters.AddWithValue("$id", id.ToString());
            string title;
            string? model;
            string? system;
            long created;
            long updated;
            using (var reader = head.ExecuteReader())
            {
                if (!reader.Read()) return null;
                title = reader.GetString(0);
                model = reader.IsDBNull(1) ? null : reader.GetString(1);
                system = reader.IsDBNull(2) ? null : reader.GetString(2);
                created = reader.GetInt64(3);
                updated = reader.GetInt64(4);
            }

            var images = new Dictionary<int, List<ChatImage>>();
            using (var pictures = connection.CreateCommand())
            {
                pictures.CommandText = "SELECT position, media_type, data FROM images WHERE conversation_id = $id ORDER BY position, ordinal";
                pictures.Parameters.AddWithValue("$id", id.ToString());
                using var reader = pictures.ExecuteReader();
                while (reader.Read())
                {
                    var position = reader.GetInt32(0);
                    if (!images.TryGetValue(position, out var list)) images[position] = list = [];
                    list.Add(new ChatImage(reader.GetString(1), (byte[])reader.GetValue(2)));
                }
            }

            var messages = new List<ChatMessage>();
            using (var rows = connection.CreateCommand())
            {
                rows.CommandText = "SELECT position, role, content, reasoning FROM messages WHERE conversation_id = $id ORDER BY position";
                rows.Parameters.AddWithValue("$id", id.ToString());
                using var reader = rows.ExecuteReader();
                while (reader.Read())
                {
                    var position = reader.GetInt32(0);
                    messages.Add(new ChatMessage((ChatRole)reader.GetInt32(1), reader.GetString(2), images.TryGetValue(position, out var list) ? list : null, reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            return new Conversation(id, title, model, system, messages, FromMillis(created), FromMillis(updated));
        }, ct);

    public Task SaveAsync(Conversation conversation, CancellationToken ct = default) =>
        OnWorkerAsync<object?>(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var id = conversation.Id.ToString();
            Execute(connection, transaction,
                "INSERT INTO conversations (id, title, model, system_prompt, created, updated) VALUES ($id, $title, $model, $system, $created, $updated) "
                + "ON CONFLICT(id) DO UPDATE SET title = $title, model = $model, system_prompt = $system, created = $created, updated = $updated",
                ("$id", id), ("$title", conversation.Title), ("$model", conversation.Model), ("$system", conversation.SystemPrompt),
                ("$created", ToMillis(conversation.Created)), ("$updated", ToMillis(conversation.Updated)));
            Execute(connection, transaction, "DELETE FROM messages WHERE conversation_id = $id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM images WHERE conversation_id = $id", ("$id", id));
            for (var position = 0; position < conversation.Messages.Count; position++)
            {
                var message = conversation.Messages[position];
                Execute(connection, transaction,
                    "INSERT INTO messages (conversation_id, position, role, content, reasoning) VALUES ($id, $position, $role, $content, $reasoning)",
                    ("$id", id), ("$position", position), ("$role", (int)message.Role), ("$content", message.Content), ("$reasoning", message.Reasoning));
                for (var ordinal = 0; ordinal < (message.Images?.Count ?? 0); ordinal++)
                {
                    var image = message.Images![ordinal];
                    Execute(connection, transaction,
                        "INSERT INTO images (conversation_id, position, ordinal, media_type, data) VALUES ($id, $position, $ordinal, $type, $data)",
                        ("$id", id), ("$position", position), ("$ordinal", ordinal), ("$type", image.MediaType), ("$data", image.Data));
                }
            }

            transaction.Commit();
            return null;
        }, ct);

    public Task DeleteAsync(Guid id, CancellationToken ct = default) =>
        OnWorkerAsync<object?>(connection =>
        {
            // Messages and images go with it (foreign keys cascade).
            Execute(connection, null, "DELETE FROM conversations WHERE id = $id", ("$id", id.ToString()));
            return null;
        }, ct);

    public void Dispose()
    {
        _gate.Dispose();
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
    }

    private async Task<T> OnWorkerAsync<T>(Func<SqliteConnection, T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var connection = Open();
                return work(connection);
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private SqliteConnection Open()
    {
        if (!_ready) Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON");
        if (!_ready)
        {
            Execute(connection, null, "PRAGMA journal_mode = WAL");
            Migrate(connection);
            _ready = true;
        }

        return connection;
    }

    private static void Migrate(SqliteConnection connection)
    {
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL)");
        int version;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT MAX(version) FROM schema_version";
            version = command.ExecuteScalar() is long v ? (int)v : 0;
        }

        if (version > CurrentVersion) throw new IOException($"The conversation database is from a newer Wicked (schema {version}, this build reads up to {CurrentVersion})");
        for (var step = version + 1; step <= CurrentVersion; step++)
        {
            using var transaction = connection.BeginTransaction();
            foreach (var statement in Steps[step - 1]) Execute(connection, transaction, statement);
            Execute(connection, transaction, "DELETE FROM schema_version");
            Execute(connection, transaction, "INSERT INTO schema_version (version) VALUES ($v)", ("$v", step));
            transaction.Commit();
            Log.Info($"Conversations: schema {step}");
        }
    }

    /// <summary>The schema, one list of statements per version. Never edit a step that has shipped; add the next one.</summary>
    internal static readonly string[][] Steps =
    [
        // 1: conversations and their messages.
        [
            "CREATE TABLE conversations (id TEXT PRIMARY KEY, title TEXT NOT NULL, model TEXT, created INTEGER NOT NULL, updated INTEGER NOT NULL)",
            "CREATE TABLE messages (conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE, position INTEGER NOT NULL, role INTEGER NOT NULL, content TEXT NOT NULL, reasoning TEXT, PRIMARY KEY (conversation_id, position))",
            "CREATE INDEX conversations_updated ON conversations(updated DESC)",
        ],
        // 2: a system prompt per conversation, and images as blobs.
        [
            "ALTER TABLE conversations ADD COLUMN system_prompt TEXT",
            "CREATE TABLE images (conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE, position INTEGER NOT NULL, ordinal INTEGER NOT NULL, media_type TEXT NOT NULL, data BLOB NOT NULL, PRIMARY KEY (conversation_id, position, ordinal))",
        ],
    ];

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long ToMillis(DateTimeOffset value) => value.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromMillis(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value);
}
