using Microsoft.Data.Sqlite;

namespace PicoQueue.Storage.Sqlite.Schema;


public static class SchemaDefinitionsCopy
{
    public const string QueueJobsTableName = "QueueJob";
    public const string ScheduleJobsTableName = "ScheduleJob";

    public const string ConfigurePragmasSql = @"
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA busy_timeout = 5000;
        PRAGMA foreign_keys = ON;
    ";

    public const string CreateQueueJobsTableSql = @"
    CREATE TABLE IF NOT EXISTS QueueJob(
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
    ON QueueJob (Queue, State, Priority DESC, CreatedAt ASC);

    CREATE INDEX IF NOT EXISTS idx_queue_jobs_heartbeat
    ON QueueJob (State, HeartbeatAt ASC)
    WHERE State = 'Processing';
    ";

    


}