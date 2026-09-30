using System.Globalization;
using Microsoft.Data.Sqlite;

namespace QnocrPoc.Extension.AgentContext;

/// <summary>File-backed atomic snapshots; one owning manager, short-lived non-pooled connections.</summary>
public sealed class SQLiteAgentSessionStore : IAgentSessionStore
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public SQLiteAgentSessionStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath == ":memory:")
            throw new ArgumentException("A durable database file path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true, Pooling = false, DefaultTimeout = 5
        }.ToString();
        InitializeSchema();
    }

    public string DatabasePath { get; }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private void InitializeSchema()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var versionQuery = Command(connection, transaction, "PRAGMA user_version;");
        var version = Convert.ToInt32(versionQuery.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version != 0 && version != AgentSessionPersistenceModel.SchemaVersion)
            throw new InvalidDataException($"Unsupported Agent session schema version {version}.");
        if (version == 0)
        {
            using var existing = Command(connection, transaction,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';");
            if (Convert.ToInt32(existing.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InvalidDataException("Refusing to initialize an unrelated database.");
            using var schema = Command(connection, transaction, """
                CREATE TABLE Sessions (
                    SessionId TEXT PRIMARY KEY NOT NULL COLLATE BINARY,
                    Source TEXT NOT NULL COLLATE BINARY,
                    SellerId TEXT NOT NULL COLLATE BINARY,
                    BuyerId TEXT NOT NULL COLLATE BINARY,
                    IdentityLevel INTEGER NOT NULL CHECK (IdentityLevel = 0),
                    State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 2),
                    CreatedTime TEXT NOT NULL,
                    LastActiveTime TEXT NOT NULL
                );
                CREATE UNIQUE INDEX OneActiveSessionPerIdentity
                    ON Sessions(Source, SellerId, BuyerId) WHERE State = 0;
                CREATE TABLE Messages (
                    SessionId TEXT NOT NULL REFERENCES Sessions(SessionId) ON DELETE CASCADE,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal >= 0),
                    MessageId TEXT NOT NULL COLLATE BINARY,
                    Timestamp INTEGER NOT NULL,
                    TimestampKind INTEGER NOT NULL CHECK (TimestampKind BETWEEN 0 AND 2),
                    Content TEXT NOT NULL,
                    Source TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    PRIMARY KEY (SessionId, Ordinal),
                    UNIQUE (SessionId, MessageId)
                );
                CREATE TABLE StateHistory (
                    SessionId TEXT NOT NULL REFERENCES Sessions(SessionId) ON DELETE CASCADE,
                    Ordinal INTEGER NOT NULL CHECK (Ordinal BETWEEN 0 AND 2),
                    OldState INTEGER NULL CHECK (OldState BETWEEN 0 AND 2),
                    NewState INTEGER NOT NULL CHECK (NewState BETWEEN 0 AND 2),
                    Reason TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    PRIMARY KEY (SessionId, Ordinal)
                );
                PRAGMA user_version = 1;
                """);
            schema.ExecuteNonQuery();
        }
        transaction.Commit();
        using var journal = Command(connection, null, "PRAGMA journal_mode=WAL;");
        journal.ExecuteScalar();
    }

    public void SaveSession(AgentSession session)
    {
        var snapshot = AgentSessionPersistenceModel.Snapshot(session);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using (var upsert = Command(connection, transaction, """
                INSERT INTO Sessions(SessionId, Source, SellerId, BuyerId, IdentityLevel, State, CreatedTime, LastActiveTime)
                VALUES($id, $source, $seller, $buyer, $level, $state, $created, $active)
                ON CONFLICT(SessionId) DO UPDATE SET Source=excluded.Source, SellerId=excluded.SellerId,
                    BuyerId=excluded.BuyerId, IdentityLevel=excluded.IdentityLevel, State=excluded.State,
                    CreatedTime=excluded.CreatedTime, LastActiveTime=excluded.LastActiveTime;
                """, ("$id", snapshot.SessionId), ("$source", snapshot.Identity.Source),
                ("$seller", snapshot.Identity.SellerId), ("$buyer", snapshot.Identity.BuyerId),
                ("$level", (int)snapshot.Identity.IdentityLevel), ("$state", (int)snapshot.Status),
                ("$created", Format(snapshot.CreatedTime)), ("$active", Format(snapshot.LastActiveTime))))
                upsert.ExecuteNonQuery();
            using (var clear = Command(connection, transaction,
                "DELETE FROM Messages WHERE SessionId=$id; DELETE FROM StateHistory WHERE SessionId=$id;",
                ("$id", snapshot.SessionId))) clear.ExecuteNonQuery();
            for (var index = 0; index < snapshot.Messages.Count; index++)
            {
                var message = snapshot.Messages[index];
                using var insert = Command(connection, transaction, """
                    INSERT INTO Messages(SessionId, Ordinal, MessageId, Timestamp, TimestampKind, Content, Source, Role)
                    VALUES($session, $ordinal, $id, $time, $kind, $content, $source, $role);
                    """, ("$session", snapshot.SessionId), ("$ordinal", index), ("$id", message.MessageId),
                    ("$time", message.Timestamp.Ticks), ("$kind", (int)message.Timestamp.Kind),
                    ("$content", message.Content), ("$source", message.Source), ("$role", message.Role));
                insert.ExecuteNonQuery();
            }
            for (var index = 0; index < snapshot.StateHistory.Count; index++)
            {
                var change = snapshot.StateHistory[index];
                using var insert = Command(connection, transaction, """
                    INSERT INTO StateHistory(SessionId, Ordinal, OldState, NewState, Reason, Timestamp)
                    VALUES($session, $ordinal, $old, $new, $reason, $time);
                    """, ("$session", snapshot.SessionId), ("$ordinal", index),
                    ("$old", change.From is { } from ? (int)from : null), ("$new", (int)change.To),
                    ("$reason", change.Reason), ("$time", Format(change.ChangedTime)));
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public AgentSession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate) return ReadSnapshots(sessionId).SingleOrDefault();
    }

    public IReadOnlyList<AgentSession> LoadSessions()
    {
        lock (_gate) return ReadSnapshots(null);
    }

    private IReadOnlyList<AgentSession> ReadSnapshots(string? sessionId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var snapshots = new List<AgentSession>();
        using (var query = Command(connection, transaction, """
            SELECT SessionId, Source, SellerId, BuyerId, IdentityLevel, State, CreatedTime, LastActiveTime
            FROM Sessions
            """ + (sessionId is null ? " ORDER BY CreatedTime, SessionId;" : " WHERE SessionId=$id;"),
            sessionId is null ? [] : [("$id", sessionId)]))
        using (var reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetInt32(4) != (int)IdentityLevel.Observed)
                    throw new InvalidDataException("Persisted identity level must remain Observed.");
                snapshots.Add(new AgentSession(reader.GetString(0), Parse(reader.GetString(6)), Parse(reader.GetString(7)),
                    [], new SessionIdentity(reader.GetString(1), reader.GetString(2), reader.GetString(3)),
                    (AgentSessionStatus)reader.GetInt32(5)));
            }
        }
        for (var index = 0; index < snapshots.Count; index++)
        {
            var snapshot = snapshots[index];
            var messages = new List<AgentSessionMessage>();
            using (var query = Command(connection, transaction, """
                SELECT Ordinal, MessageId, Timestamp, TimestampKind, Content, Source, Role
                FROM Messages WHERE SessionId=$id ORDER BY Ordinal;
                """, ("$id", snapshot.SessionId)))
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetInt32(0) != messages.Count || reader.GetInt32(3) is < 0 or > 2)
                        throw new InvalidDataException("Invalid persisted message ordering or timestamp kind.");
                    messages.Add(new AgentSessionMessage(reader.GetString(1),
                        new DateTime(reader.GetInt64(2), (DateTimeKind)reader.GetInt32(3)),
                        reader.GetString(4), reader.GetString(5), snapshot.SessionId, reader.GetString(6)));
                }
            }
            var history = new List<AgentSessionStateChange>();
            using (var query = Command(connection, transaction, """
                SELECT Ordinal, OldState, NewState, Reason, Timestamp
                FROM StateHistory WHERE SessionId=$id ORDER BY Ordinal;
                """, ("$id", snapshot.SessionId)))
            using (var reader = query.ExecuteReader())
            {
                while (reader.Read())
                {
                    if (reader.GetInt32(0) != history.Count)
                        throw new InvalidDataException("Invalid persisted state history ordering.");
                    history.Add(new AgentSessionStateChange(reader.IsDBNull(1) ? null :
                        (AgentSessionStatus)reader.GetInt32(1), (AgentSessionStatus)reader.GetInt32(2),
                        Parse(reader.GetString(4)), reader.GetString(3)));
                }
            }
            snapshots[index] = AgentSessionPersistenceModel.Snapshot(snapshot with
            {
                Messages = messages, StateHistory = history
            });
        }
        transaction.Commit();
        return snapshots.AsReadOnly();
    }

    public bool DeleteSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var delete = Command(connection, transaction, "DELETE FROM Sessions WHERE SessionId=$id;", ("$id", sessionId));
            var deleted = delete.ExecuteNonQuery() != 0;
            transaction.Commit();
            return deleted;
        }
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string value) =>
        DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None);
}
