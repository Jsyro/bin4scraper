using System.Data;
using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Bin4Scraper.Data;

/// <summary>SQLite persistence for recipients, checks and deliveries.</summary>
public sealed class Store
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS recipients (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            phone TEXT NOT NULL UNIQUE,
            enabled INTEGER NOT NULL DEFAULT 1,
            created_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS checks (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            started_at TEXT NOT NULL,
            completed_at TEXT,
            triggered_by TEXT NOT NULL,
            status TEXT NOT NULL,
            title TEXT,
            fingerprint TEXT,
            items_json TEXT,
            message TEXT,
            changed INTEGER NOT NULL DEFAULT 0,
            error TEXT,
            snapshot_file TEXT
        );

        CREATE TABLE IF NOT EXISTS deliveries (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            check_id INTEGER,
            recipient_id INTEGER,
            phone TEXT NOT NULL,
            kind TEXT NOT NULL,
            message TEXT NOT NULL,
            status TEXT NOT NULL,
            attempts INTEGER NOT NULL DEFAULT 0,
            provider TEXT,
            provider_message_id TEXT,
            error TEXT,
            created_at TEXT NOT NULL,
            last_attempt_at TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_deliveries_status ON deliveries(status);
        CREATE INDEX IF NOT EXISTS ix_checks_status ON checks(status);
        """;

    private const string DeliveryColumns = """
        check_id, recipient_id, phone, kind, message, status, attempts, provider, provider_message_id, error, created_at, last_attempt_at
        """;

    private const string DeliveryValues = """
        @CheckId, @RecipientId, @Phone, @Kind, @Message, @Status, @Attempts, @Provider, @ProviderMessageId, @Error, @CreatedAt, @LastAttemptAt
        """;

    private readonly string _connectionString;

    static Store()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        SqlMapper.AddTypeHandler(new DateTimeOffsetHandler());
    }

    public Store(IOptions<StorageOptions> options)
    {
        DataDirectory = Path.GetFullPath(options.Value.DataDirectory);
        DatabasePath = Path.Combine(DataDirectory, "bin4scraper.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString();
    }

    public string DataDirectory { get; }
    public string DatabasePath { get; }

    public void Initialize()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            var probe = Path.Combine(DataDirectory, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Data directory '{DataDirectory}' is not writable: {ex.Message}", ex);
        }

        using var connection = Open();
        connection.Execute("PRAGMA journal_mode=WAL;");
        connection.Execute(Schema);
    }

    // Checks

    public long StartCheck(string triggeredBy, DateTimeOffset startedAt)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            "INSERT INTO checks (started_at, triggered_by, status) VALUES (@startedAt, @triggeredBy, @status); SELECT last_insert_rowid();",
            new { startedAt, triggeredBy, status = CheckStatus.Running });
    }

    /// <summary>Saves the final state of a check and queues its deliveries atomically.</summary>
    public void CompleteCheck(CheckRun check, IReadOnlyList<Delivery> deliveries)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        connection.Execute("""
            UPDATE checks SET completed_at = @CompletedAt, status = @Status, title = @Title, fingerprint = @Fingerprint,
                items_json = @ItemsJson, message = @Message, changed = @Changed, error = @Error, snapshot_file = @SnapshotFile
            WHERE id = @Id
            """, check, transaction);
        foreach (var delivery in deliveries)
        {
            delivery.Id = InsertDelivery(connection, delivery, transaction);
        }
        transaction.Commit();
    }

    public CheckRun? GetLatestCheck()
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<CheckRun>("SELECT * FROM checks ORDER BY id DESC LIMIT 1");
    }

    public CheckRun? GetLatestSucceededCheck(long? excludingId = null)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<CheckRun>(
            "SELECT * FROM checks WHERE status = @status AND (@excludingId IS NULL OR id <> @excludingId) ORDER BY id DESC LIMIT 1",
            new { status = CheckStatus.Succeeded, excludingId });
    }

    public IReadOnlyList<CheckRun> GetRecentChecks(int count)
    {
        using var connection = Open();
        return connection.Query<CheckRun>("SELECT * FROM checks ORDER BY id DESC LIMIT @count", new { count }).AsList();
    }

    /// <summary>Number of failed checks since the most recent successful one.</summary>
    public int CountConsecutiveFailures()
    {
        using var connection = Open();
        return connection.ExecuteScalar<int>("""
            SELECT COUNT(*) FROM checks
            WHERE status = @failed AND id > COALESCE((SELECT MAX(id) FROM checks WHERE status = @succeeded), 0)
            """, new { failed = CheckStatus.Failed, succeeded = CheckStatus.Succeeded });
    }

    // Recipients

    public IReadOnlyList<Recipient> GetRecipients()
    {
        using var connection = Open();
        return connection.Query<Recipient>("SELECT * FROM recipients ORDER BY name COLLATE NOCASE, id").AsList();
    }

    public Recipient? GetRecipient(long id)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<Recipient>("SELECT * FROM recipients WHERE id = @id", new { id });
    }

    public long AddRecipient(string name, string phone)
    {
        using var connection = Open();
        return connection.ExecuteScalar<long>(
            "INSERT INTO recipients (name, phone, enabled, created_at) VALUES (@name, @phone, 1, @createdAt); SELECT last_insert_rowid();",
            new { name, phone, createdAt = DateTimeOffset.UtcNow });
    }

    public void SetRecipientEnabled(long id, bool enabled)
    {
        using var connection = Open();
        connection.Execute("UPDATE recipients SET enabled = @enabled WHERE id = @id", new { id, enabled });
    }

    public void DeleteRecipient(long id)
    {
        using var connection = Open();
        connection.Execute("DELETE FROM recipients WHERE id = @id", new { id });
    }

    // Deliveries

    public void AddDelivery(Delivery delivery)
    {
        using var connection = Open();
        delivery.Id = InsertDelivery(connection, delivery, null);
    }

    public void UpdateDelivery(Delivery delivery)
    {
        using var connection = Open();
        connection.Execute("""
            UPDATE deliveries SET status = @Status, attempts = @Attempts, provider = @Provider,
                provider_message_id = @ProviderMessageId, error = @Error, last_attempt_at = @LastAttemptAt
            WHERE id = @Id
            """, delivery);
    }

    public Delivery? GetDelivery(long id)
    {
        using var connection = Open();
        return connection.QueryFirstOrDefault<Delivery>("SELECT * FROM deliveries WHERE id = @id", new { id });
    }

    public List<Delivery> GetPendingDeliveries()
    {
        using var connection = Open();
        return connection.Query<Delivery>("SELECT * FROM deliveries WHERE status = @status ORDER BY id",
            new { status = DeliveryStatus.Pending }).AsList();
    }

    public IReadOnlyList<Delivery> GetRecentDeliveries(int count)
    {
        using var connection = Open();
        return connection.Query<Delivery>("SELECT * FROM deliveries ORDER BY id DESC LIMIT @count", new { count }).AsList();
    }

    public void BackupTo(string path)
    {
        using var source = Open();
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static long InsertDelivery(SqliteConnection connection, Delivery delivery, IDbTransaction? transaction) =>
        connection.ExecuteScalar<long>(
            $"INSERT INTO deliveries ({DeliveryColumns}) VALUES ({DeliveryValues}); SELECT last_insert_rowid();",
            delivery, transaction);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private sealed class DateTimeOffsetHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value) =>
            parameter.Value = value.ToString("O", CultureInfo.InvariantCulture);

        public override DateTimeOffset Parse(object value) =>
            DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
