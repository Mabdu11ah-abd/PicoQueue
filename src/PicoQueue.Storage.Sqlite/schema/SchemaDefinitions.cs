using Microsoft.Data.Sqlite;

namespace PicoQueue.Storage.Sqlite.Schema;

/// <summary>
/// Contains SQL DDL schema definitions, PRAGMA configurations, and methods to ensure tables and indexes are initialized.
/// </summary>
public static class SchemaDefinitions
{
    public const string QueueJobsTableName = "QueueJobs";
    public const string ScheduledJobsTableName = "ScheduledJobs";

    public static class Tables
    {
        public const string QueueJobs = QueueJobsTableName;
        public const string ScheduledJobs = ScheduledJobsTableName;
    }

    public const string ConfigurePragmasSql = @"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = 5000;
        PRAGMA foreign_keys = ON;
    ";

    public const string CreateQueueJobsTableSql = @"
        CREATE TABLE IF NOT EXISTS QueueJobs (
            Id TEXT PRIMARY KEY NOT NULL,
            Name TEXT NOT NULL, 
            Description TEXT NULL,
            TypeName TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            Queue TEXT NOT NULL DEFAULT 'default',
            State TEXT NOT NULL,
            Priority INTEGER NOT NULL DEFAULT 0,
            StartedAt TEXT NULL,
            CompletedAt TEXT NULL,
            HeartbeatAt TEXT NULL,
            Attempt INTEGER NOT NULL DEFAULT 0,
            MaxAttempts INTEGER NOT NULL DEFAULT 3,
            RetryAt TEXT NULL,
            ErrorMessage TEXT NULL,
            ErrorDetail TEXT NULL,
            WorkerId TEXT NULL,
            ScheduledJobId TEXT NULL,
            CreatedAt TEXT NOT NULL,
            LastUpdate TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_queue_jobs_poll 
        ON QueueJobs (Queue, State, Priority DESC, CreatedAt ASC);

        CREATE INDEX IF NOT EXISTS idx_queue_jobs_heartbeat 
        ON QueueJobs (State, HeartbeatAt ASC)
        WHERE State = 'Processing';
    ";

    public const string CreateScheduledJobsTableSql = @"
        CREATE TABLE IF NOT EXISTS ScheduledJobs (
            Id TEXT PRIMARY KEY NOT NULL,
            Name TEXT NOT NULL,
            Description TEXT NULL,
            TypeName TEXT NOT NULL,
            PayloadJson TEXT NOT NULL,
            Queue TEXT NOT NULL DEFAULT 'default',
            ScheduledAt TEXT NOT NULL,
            IsRecurring INTEGER NOT NULL DEFAULT 0,
            CronExpression TEXT NULL,
            LastTriggeredAt TEXT NULL,
            NextRunAt TEXT NULL,
            IsEnabled INTEGER NOT NULL DEFAULT 1,
            CreatedAt TEXT NOT NULL,
            LastUpdate TEXT NOT NULL,
            UpdatedAt TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_scheduled_jobs_due 
        ON ScheduledJobs (IsEnabled, NextRunAt ASC)
        WHERE IsEnabled = 1;
    ";

    public const string CompleteInitializationSql = 
        ConfigurePragmasSql + "\n" + 
        CreateQueueJobsTableSql + "\n" + 
        CreateScheduledJobsTableSql;

    /// <summary>
    /// Executes schema creation scripts and applies PRAGMAs to ensure database tables and indexes exist.
    /// </summary>
    public static async Task EnsureInitializedAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = CompleteInitializationSql;
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronously executes schema creation scripts and applies PRAGMAs to ensure database tables and indexes exist.
    /// </summary>
    public static void EnsureInitialized(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = CompleteInitializationSql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Extension method on <see cref="SqliteConnection"/> to ensure schema and PRAGMAs are initialized.
    /// </summary>
    public static Task EnsureSchemaInitializedAsync(this SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        return EnsureInitializedAsync(connection, cancellationToken);
    }
}
