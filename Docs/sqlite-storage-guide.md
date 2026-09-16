# SQLite Storage Implementation Guide using Microsoft.Data.Sqlite

This guide provides an in-depth, hands-on reference for implementing the **PicoQueue** storage backend (`PicoQueue.Storage.Sqlite`) using **`Microsoft.Data.Sqlite`**.

---

## Table of Contents

1. [Introduction to `Microsoft.Data.Sqlite`](#1-introduction-to-microsoftdatasqlite)
   - [Why Raw ADO.NET Over EF Core for Job Queues?](#why-raw-adonet-over-ef-core-for-job-queues)
   - [Package Installation](#package-installation)
2. [SQLite Concurrency & Engine Fundamentals](#2-sqlite-concurrency--engine-fundamentals)
   - [The Single-Writer, Multi-Reader Model](#the-single-writer-multi-reader-model)
   - [Essential PRAGMAs (WAL Mode, Synchronous, Busy Timeout)](#essential-pragmas-wal-mode-synchronous-busy-timeout)
   - [Thread Safety & Connection Lifecycles](#thread-safety--connection-lifecycles)
3. [Type Mapping: C# to SQLite](#3-type-mapping-c-to-sqlite)
   - [Data Types Overview](#data-types-overview)
   - [Handling `DateTimeOffset`](#handling-datetimeoffset)
   - [Handling `Guid`](#handling-guid)
   - [Handling Enums, Booleans, and Nullables](#handling-enums-booleans-and-nullables)
4. [Core API Syntax & Methods](#4-core-api-syntax--methods)
   - [Connection Strings: `SqliteConnectionStringBuilder`](#connection-strings-sqliteconnectionstringbuilder)
   - [Opening Connections: `SqliteConnection`](#opening-connections-sqliteconnection)
   - [Executing Commands: `SqliteCommand`](#executing-commands-sqlitecommand)
   - [Safe Parameterization: `SqliteParameter`](#safe-parameterization-sqliteparameter)
   - [Streaming Rows: `SqliteDataReader`](#streaming-rows-sqlitedatareader)
   - [Managing Transactions: `SqliteTransaction` & `BEGIN IMMEDIATE`](#managing-transactions-sqlitetransaction--begin-immediate)
5. [Database Schema for PicoQueue](#5-database-schema-for-picoqueue)
   - [Table: `QueueJobs`](#table-queuejobs)
   - [Table: `ScheduledJobs`](#table-scheduledjobs)
   - [Performance Indexes](#performance-indexes)
6. [End-to-End Implementation Scenarios](#6-end-to-end-implementation-scenarios)
   - [Scenario 1: Schema Initialization & PRAGMA Setup](#scenario-1-schema-initialization--pragma-setup)
   - [Scenario 2: Enqueuing a Job (`EnqueueAsync`)](#scenario-2-enqueuing-a-job-enqueueasync)
   - [Scenario 3: Atomic Job Dequeue & Locking (`DequeueAsync`)](#scenario-3-atomic-job-dequeue--locking-dequeueasync)
   - [Scenario 4: Worker Heartbeats & Liveness (`UpdateHeartbeatAsync`)](#scenario-4-worker-heartbeats--liveness-updateheartbeatasync)
   - [Scenario 5: Job Completion & Failure Handling (`Acknowledge*`)](#scenario-5-job-completion--failure-handling-acknowledge)
   - [Scenario 6: Orphaned Job Recovery (Crash Recovery)](#scenario-6-orphaned-job-recovery-crash-recovery)
   - [Scenario 7: Paginated Querying (`GetJobsAsync`)](#scenario-7-paginated-querying-getjobsasync)
   - [Scenario 8: Managing Scheduled & Recurring Jobs (`IScheduleJob`)](#scenario-8-managing-scheduled--recurring-jobs-ischedulejob)
7. [Production Best Practices & Gotchas](#7-production-best-practices--gotchas)
   - [Handling `SQLITE_BUSY` and Concurrency Locks](#handling-sqlite_busy-and-concurrency-locks)
   - [Avoiding Data Reader Leaks with `await using`](#avoiding-data-reader-leaks-with-await-using)
   - [Unit Testing with In-Memory SQLite](#unit-testing-with-in-memory-sqlite)

---

## 1. Introduction to `Microsoft.Data.Sqlite`

`Microsoft.Data.Sqlite` is the official Microsoft low-level ADO.NET data provider for SQLite. It is lightweight, cross-platform, fast, and provides direct access to SQLite's native C API via SQLitePCLRaw.

### Why Raw ADO.NET Over EF Core for Job Queues?

1. **Zero Overhead:** Background queues poll, lock, update, and acknowledge hundreds or thousands of times per minute. EF Core's Change Tracker, identity map, and query translation add unnecessary latency and memory allocations.
2. **Deterministic Locking:** SQLite requires specific transactional semantics (`BEGIN IMMEDIATE`) and locking pragmas for atomic job dequeuing that are difficult or impossible to control cleanly via EF Core.
3. **No Migration Bloat:** PicoQueue is an embedded library. Creating a lightweight table if it does not exist (`CREATE TABLE IF NOT EXISTS`) keeps user installations completely frictionless (zero manual migrations required).

### Package Installation

In `src/PicoQueue.Storage.Sqlite/PicoQueue.Storage.Sqlite.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.11" />
</ItemGroup>
```

Namespace import:
```csharp
using Microsoft.Data.Sqlite;
```

---

## 2. SQLite Concurrency & Engine Fundamentals

### The Single-Writer, Multi-Reader Model

By default, SQLite locks the entire database file during writes. In a job queue, multiple worker threads (or processes) are continuously attempting to write (dequeuing jobs, updating heartbeats, completing jobs). Without tuning, concurrent writes will throw:

```text
Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is locked'.
```

### Essential PRAGMAs (WAL Mode, Synchronous, Busy Timeout)

Every SQLite database used for queuing **must** configure these four settings upon opening or during initialization:

```sql
-- 1. Enable Write-Ahead Logging: readers never block writers, writers never block readers
PRAGMA journal_mode = WAL;

-- 2. Synchronous Normal: safe against app crashes, drastically improves write throughput in WAL mode
PRAGMA synchronous = NORMAL;

-- 3. Busy Timeout: tell SQLite to wait up to N milliseconds for locks instead of throwing immediately
PRAGMA busy_timeout = 5000;

-- 4. Foreign Keys: enforce relational integrity
PRAGMA foreign_keys = ON;
```

### Thread Safety & Connection Lifecycles

> [!WARNING]
> An open `SqliteConnection` is **NOT** thread-safe. Never share a single open `SqliteConnection` instance across multiple concurrent threads or tasks.

**Recommended Pattern:**
- Store the **connection string** in your storage backend service (e.g., `SqliteJobQueue : IJobQueue`).
- Create and open a `SqliteConnection` per operation (or per transaction), wrapped in `await using`.
- `Microsoft.Data.Sqlite` automatically manages internal connection pooling for file-based databases.

---

## 3. Type Mapping: C# to SQLite

SQLite has only 5 fundamental storage classes: `NULL`, `INTEGER`, `REAL`, `TEXT`, and `BLOB`.

| C# Type | SQLite Storage Class | Recommended Column Definition | Notes |
| :--- | :--- | :--- | :--- |
| `Guid` | `TEXT` | `TEXT NOT NULL` | Formatted as string (e.g. `d` format: `36` chars) |
| `DateTimeOffset` | `TEXT` | `TEXT` | Formatted as ISO 8601 (`"O"` format: `yyyy-MM-ddTHH:mm:ss.fffffffzzz`) |
| `DateTimeOffset?` | `TEXT` | `TEXT NULL` | Nullable timestamp |
| `bool` | `INTEGER` | `INTEGER NOT NULL` | `1` for true, `0` for false |
| `JobState` (Enum) | `TEXT` or `INTEGER`| `TEXT NOT NULL` | Storing as string (e.g. `'Enqueued'`) improves manual debugging |
| `int`, `long` | `INTEGER` | `INTEGER NOT NULL` | Int32 or Int64 |
| `string` | `TEXT` | `TEXT` | JSON payloads, error messages, queues |

### Handling `DateTimeOffset`

Always serialize `DateTimeOffset` in round-trip format (`"O"`):

```csharp
// Writing to DB
var isoString = dateTimeOffset.ToString("O"); // "2026-09-05T10:15:30.1234567+00:00"

// Reading from DB
var raw = reader.GetString(ordinal);
var dt = DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
```

> [!TIP]
> ISO 8601 strings sort lexicographically. SQLite's standard comparison operators (`WHERE ScheduledAt <= @Now`) work as expected with ISO strings.

### Handling `Guid`

```csharp
// Writing to DB
cmd.Parameters.AddWithValue("@Id", id.ToString());

// Reading from DB
var guid = Guid.Parse(reader.GetString(ordinal));
```

### Handling Enums, Booleans, and Nullables

```csharp
// Enum to string
cmd.Parameters.AddWithValue("@State", job.State.ToString());

// String back to Enum
var state = Enum.Parse<JobState>(reader.GetString(stateOrdinal));

// Nullables: DBNull.Value must be passed if value is null
cmd.Parameters.AddWithValue("@ErrorMessage", (object?)job.ErrorMessage ?? DBNull.Value);

// Reading nullable fields
string? errorMessage = reader.IsDBNull(errorOrdinal) ? null : reader.GetString(errorOrdinal);
```

---

## 4. Core API Syntax & Methods

### Connection Strings: `SqliteConnectionStringBuilder`

Use `SqliteConnectionStringBuilder` to construct safe connection strings:

```csharp
var builder = new SqliteConnectionStringBuilder
{
    DataSource = "picoqueue.db",
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Default,
    DefaultTimeout = 5 // In seconds
};

string connectionString = builder.ToString();
```

### Opening Connections: `SqliteConnection`

Always dispose connections asynchronously using `await using`:

```csharp
await using var connection = new SqliteConnection(connectionString);
await connection.OpenAsync(cancellationToken);
```

### Executing Commands: `SqliteCommand`

```csharp
await using var command = connection.CreateCommand();
command.CommandText = "SELECT COUNT(*) FROM QueueJobs;";
```

There are three primary execution methods:

| Method | When to Use | Return Value |
| :--- | :--- | :--- |
| `ExecuteNonQueryAsync(ct)` | `INSERT`, `UPDATE`, `DELETE`, DDL statements | `int` (number of affected rows) |
| `ExecuteScalarAsync(ct)` | Queries returning a single value (e.g. `COUNT(*)`, `MAX()`, single column) | `object?` (cast to target type) |
| `ExecuteReaderAsync(ct)` | Queries returning multiple rows and columns | `SqliteDataReader` |

### Safe Parameterization: `SqliteParameter`

Never concatenate user inputs into SQL strings. Always use parameters.

```csharp
// Method A: AddWithValue (common, concise)
command.Parameters.AddWithValue("@Queue", queueName);
command.Parameters.AddWithValue("@Error", (object?)errorDetails ?? DBNull.Value);

// Method B: Explicit SqliteParameter (highest performance, prevents type inference)
var param = command.CreateParameter();
param.ParameterName = "@Queue";
param.SqliteType = SqliteType.Text;
param.Value = queueName;
command.Parameters.Add(param);
```

### Streaming Rows: `SqliteDataReader`

Read rows sequentially using `await reader.ReadAsync(ct)`:

```csharp
await using var reader = await command.ExecuteReaderAsync(cancellationToken);

// Cache column ordinals before the loop for maximum performance
int idOrdinal = reader.GetOrdinal("Id");
int nameOrdinal = reader.GetOrdinal("Name");
int attemptsOrdinal = reader.GetOrdinal("Attempt");
int retryAtOrdinal = reader.GetOrdinal("RetryAt");

while (await reader.ReadAsync(cancellationToken))
{
    var id = Guid.Parse(reader.GetString(idOrdinal));
    var name = reader.GetString(nameOrdinal);
    var attempt = reader.GetInt32(attemptsOrdinal);
    
    DateTimeOffset? retryAt = reader.IsDBNull(retryAtOrdinal)
        ? null
        : DateTimeOffset.Parse(reader.GetString(retryAtOrdinal));
}
```

### Managing Transactions: `SqliteTransaction` & `BEGIN IMMEDIATE`

By default, SQLite transactions are `DEFERRED`. A `DEFERRED` transaction starts in read-only mode and attempts to upgrade to a write lock only when the first `UPDATE` or `INSERT` is executed.

If two worker threads read a job at the same time and both try to update it, **both will fail with `SQLITE_BUSY`** because neither can upgrade its lock.

> [!IMPORTANT]
> For queue operations like `DequeueAsync`, you **must** acquire a write lock immediately when starting the transaction. In raw SQLite this is `BEGIN IMMEDIATE`.

In `Microsoft.Data.Sqlite`, you execute `BEGIN IMMEDIATE` explicitly:

```csharp
// Acquire immediate write lock so no other worker can dequeue concurrently
await using var cmd = connection.CreateCommand();
cmd.CommandText = "BEGIN IMMEDIATE;";
await cmd.ExecuteNonQueryAsync(cancellationToken);

try
{
    // ... select next job and update to 'Processing' ...
    
    cmd.CommandText = "COMMIT;";
    await cmd.ExecuteNonQueryAsync(cancellationToken);
}
catch
{
    cmd.CommandText = "ROLLBACK;";
    await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    throw;
}
```

---

## 5. Database Schema for PicoQueue

### Table: `QueueJobs`

Stores active, processing, succeeded, and failed jobs:

```sql
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
```

### Table: `ScheduledJobs`

Stores definitions for delayed and recurring (cron) jobs:

```sql
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
```

### Performance Indexes

These indexes allow millisecond-level queue polling and status filtering:

```sql
-- Fast polling index for eligible queued jobs
CREATE INDEX IF NOT EXISTS idx_queue_jobs_poll 
ON QueueJobs (Queue, State, Priority DESC, CreatedAt ASC);

-- Index for scheduled job scheduler loop
CREATE INDEX IF NOT EXISTS idx_scheduled_jobs_due 
ON ScheduledJobs (IsEnabled, NextRunAt ASC)
WHERE IsEnabled = 1;

-- Index for orphaned job recovery
CREATE INDEX IF NOT EXISTS idx_queue_jobs_heartbeat 
ON QueueJobs (State, HeartbeatAt ASC)
WHERE State = 'Processing';
```

---

## 6. End-to-End Implementation Scenarios

Here is how each contract method in `IJobQueue` and `IScheduleJob` is implemented using `Microsoft.Data.Sqlite`.

### Scenario 1: Schema Initialization & PRAGMA Setup

```csharp
public class SqliteJobQueue : IJobQueue
{
    private readonly string _connectionString;

    public SqliteJobQueue(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);

        // 1. Configure SQLite Pragmas
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = @"
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
            ";
            await pragmaCmd.ExecuteNonQueryAsync(ct);
        }

        // 2. Create Tables and Indexes
        await using (var schemaCmd = connection.CreateCommand())
        {
            schemaCmd.CommandText = @"
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
            await schemaCmd.ExecuteNonQueryAsync(ct);
        }
    }
}
```

---

### Scenario 2: Enqueuing a Job (`EnqueueAsync`)

Creates and stores a new `QueueJob`:

```csharp
public async Task<QueueJob> EnqueueAsync(EnqueueJobDto dto, CancellationToken ct = default)
{
    var now = DateTimeOffset.UtcNow;
    var job = new QueueJob
    {
        Id = Guid.NewGuid(),
        Name = dto.Name ?? dto.TypeName,
        Description = dto.Description,
        TypeName = dto.TypeName,
        PayloadJson = dto.PayloadJson,
        Queue = string.IsNullOrWhiteSpace(dto.Queue) ? "default" : dto.Queue,
        State = JobState.Enqueued,
        Priority = dto.Priority,
        MaxAttempts = dto.MaxAttempts,
        Attempt = 0,
        CreatedAt = now,
        LastUpdate = now
    };

    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    const string sql = @"
        INSERT INTO QueueJobs (
            Id, Name, Description, TypeName, PayloadJson, Queue, State, 
            Priority, Attempt, MaxAttempts, CreatedAt, LastUpdate
        ) VALUES (
            @Id, @Name, @Description, @TypeName, @PayloadJson, @Queue, @State, 
            @Priority, @Attempt, @MaxAttempts, @CreatedAt, @LastUpdate
        );";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@Id", job.Id.ToString());
    cmd.Parameters.AddWithValue("@Name", job.Name);
    cmd.Parameters.AddWithValue("@Description", (object?)job.Description ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@TypeName", job.TypeName);
    cmd.Parameters.AddWithValue("@PayloadJson", job.PayloadJson);
    cmd.Parameters.AddWithValue("@Queue", job.Queue);
    cmd.Parameters.AddWithValue("@State", job.State.ToString());
    cmd.Parameters.AddWithValue("@Priority", job.Priority);
    cmd.Parameters.AddWithValue("@Attempt", job.Attempt);
    cmd.Parameters.AddWithValue("@MaxAttempts", job.MaxAttempts);
    cmd.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
    cmd.Parameters.AddWithValue("@LastUpdate", job.LastUpdate.ToString("O"));

    await cmd.ExecuteNonQueryAsync(ct);
    return job;
}
```

---

### Scenario 3: Atomic Job Dequeue & Locking (`DequeueAsync`)

This is the most critical operation: fetching the next eligible job and locking it with `State = 'Processing'` without race conditions between multiple workers.

```csharp
public async Task<QueueJob?> DequeueAsync(DequeueJobDto dto, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    // Acquire an immediate exclusive lock for the transaction
    await using (var beginCmd = connection.CreateCommand())
    {
        beginCmd.CommandText = "BEGIN IMMEDIATE;";
        await beginCmd.ExecuteNonQueryAsync(ct);
    }

    try
    {
        var now = DateTimeOffset.UtcNow;
        var nowIso = now.ToString("O");

        // 1. Find the next highest priority due job
        const string selectSql = @"
            SELECT Id FROM QueueJobs
            WHERE Queue = @Queue
              AND (
                  State = 'Enqueued'
                  OR (State = 'Retrying' AND (RetryAt IS NULL OR RetryAt <= @Now))
              )
            ORDER BY Priority DESC, CreatedAt ASC
            LIMIT 1;";

        string? jobIdStr = null;
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = selectSql;
            selectCmd.Parameters.AddWithValue("@Queue", dto.Queue);
            selectCmd.Parameters.AddWithValue("@Now", nowIso);

            var result = await selectCmd.ExecuteScalarAsync(ct);
            if (result != null && result != DBNull.Value)
            {
                jobIdStr = result.ToString();
            }
        }

        if (jobIdStr == null)
        {
            // No eligible jobs available; commit and exit
            await using var commitEmpty = connection.CreateCommand();
            commitEmpty.CommandText = "COMMIT;";
            await commitEmpty.ExecuteNonQueryAsync(ct);
            return null;
        }

        // 2. Atomically claim and lock the job
        const string updateSql = @"
            UPDATE QueueJobs
            SET State = 'Processing',
                WorkerId = @WorkerId,
                StartedAt = @StartedAt,
                HeartbeatAt = @HeartbeatAt,
                LastUpdate = @LastUpdate
            WHERE Id = @Id;

            SELECT * FROM QueueJobs WHERE Id = @Id;";

        QueueJob? claimedJob = null;
        await using (var updateCmd = connection.CreateCommand())
        {
            updateCmd.CommandText = updateSql;
            updateCmd.Parameters.AddWithValue("@WorkerId", dto.WorkerId);
            updateCmd.Parameters.AddWithValue("@StartedAt", nowIso);
            updateCmd.Parameters.AddWithValue("@HeartbeatAt", nowIso);
            updateCmd.Parameters.AddWithValue("@LastUpdate", nowIso);
            updateCmd.Parameters.AddWithValue("@Id", jobIdStr);

            await using var reader = await updateCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                claimedJob = MapQueueJobFromReader(reader);
            }
        }

        // 3. Commit the transaction
        await using (var commitCmd = connection.CreateCommand())
        {
            commitCmd.CommandText = "COMMIT;";
            await commitCmd.ExecuteNonQueryAsync(ct);
        }

        return claimedJob;
    }
    catch
    {
        await using (var rollbackCmd = connection.CreateCommand())
        {
            rollbackCmd.CommandText = "ROLLBACK;";
            await rollbackCmd.ExecuteNonQueryAsync(CancellationToken.None);
        }
        throw;
    }
}
```

---

### Scenario 4: Worker Heartbeats & Liveness (`UpdateHeartbeatAsync`)

Workers periodically call `UpdateHeartbeatAsync` to indicate that the worker process is alive and actively running the job.

```csharp
public async Task<bool> UpdateHeartbeatAsync(Guid jobId, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    var now = DateTimeOffset.UtcNow;
    const string sql = @"
        UPDATE QueueJobs
        SET HeartbeatAt = @Now,
            LastUpdate = @Now
        WHERE Id = @Id AND State = 'Processing';";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
    cmd.Parameters.AddWithValue("@Id", jobId.ToString());

    int affected = await cmd.ExecuteNonQueryAsync(ct);
    return affected > 0;
}
```

---

### Scenario 5: Job Completion & Failure Handling (`Acknowledge*`)

#### Acknowledge Success

```csharp
public async Task<bool> AcknowledgeSuccessAsync(Guid jobId, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    var now = DateTimeOffset.UtcNow;
    const string sql = @"
        UPDATE QueueJobs
        SET State = 'Succeeded',
            CompletedAt = @Now,
            LastUpdate = @Now
        WHERE Id = @Id AND State = 'Processing';";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
    cmd.Parameters.AddWithValue("@Id", jobId.ToString());

    int affected = await cmd.ExecuteNonQueryAsync(ct);
    return affected > 0;
}
```

#### Acknowledge Failure (with Retry Logic)

```csharp
public async Task<bool> AcknowledgeFailureAsync(
    Guid jobId, 
    string errorMessage, 
    string? errorDetail = null, 
    DateTimeOffset? retryAt = null, 
    CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    var now = DateTimeOffset.UtcNow;

    // Decide if job should transition to 'Retrying' or 'Failed'
    bool isRetrying = retryAt.HasValue;
    string targetState = isRetrying ? JobState.Retrying.ToString() : JobState.Failed.ToString();

    const string sql = @"
        UPDATE QueueJobs
        SET State = @TargetState,
            Attempt = Attempt + 1,
            RetryAt = @RetryAt,
            ErrorMessage = @ErrorMessage,
            ErrorDetail = @ErrorDetail,
            CompletedAt = CASE WHEN @IsRetrying = 1 THEN NULL ELSE @Now END,
            LastUpdate = @Now
        WHERE Id = @Id;";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@TargetState", targetState);
    cmd.Parameters.AddWithValue("@RetryAt", retryAt.HasValue ? (object)retryAt.Value.ToString("O") : DBNull.Value);
    cmd.Parameters.AddWithValue("@ErrorMessage", errorMessage);
    cmd.Parameters.AddWithValue("@ErrorDetail", (object?)errorDetail ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@IsRetrying", isRetrying ? 1 : 0);
    cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
    cmd.Parameters.AddWithValue("@Id", jobId.ToString());

    int affected = await cmd.ExecuteNonQueryAsync(ct);
    return affected > 0;
}
```

---

### Scenario 6: Orphaned Job Recovery (Crash Recovery)

If a worker node crashes or loses power, its job remains stuck in `Processing`. A recovery loop finds any job whose heartbeat hasn't been updated within a specified timeout (e.g. 5 minutes) and re-queues it:

```csharp
public async Task<int> RecoverOrphanedJobsAsync(TimeSpan heartbeatTimeout, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    var threshold = DateTimeOffset.UtcNow.Subtract(heartbeatTimeout).ToString("O");
    var nowIso = DateTimeOffset.UtcNow.ToString("O");

    const string sql = @"
        UPDATE QueueJobs
        SET State = 'Retrying',
            WorkerId = NULL,
            ErrorMessage = 'Recovered from orphaned/timed-out worker heartbeat',
            RetryAt = @Now,
            LastUpdate = @Now
        WHERE State = 'Processing'
          AND HeartbeatAt < @Threshold;";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@Threshold", threshold);
    cmd.Parameters.AddWithValue("@Now", nowIso);

    return await cmd.ExecuteNonQueryAsync(ct);
}
```

---

### Scenario 7: Paginated Querying (`GetJobsAsync`)

Implements filtering by queue and state with pagination using `COUNT(*)` and `LIMIT ... OFFSET`:

```csharp
public async Task<PagedResult<QueueJob>> GetJobsAsync(GetJobsFilterDto filter, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    // Build dynamic WHERE clause
    var whereClauses = new List<string>();
    var parameters = new List<SqliteParameter>();

    if (!string.IsNullOrWhiteSpace(filter.Queue))
    {
        whereClauses.Add("Queue = @Queue");
        parameters.Add(new SqliteParameter("@Queue", filter.Queue));
    }

    if (filter.State.HasValue)
    {
        whereClauses.Add("State = @State");
        parameters.Add(new SqliteParameter("@State", filter.State.Value.ToString()));
    }

    string whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : "";

    // 1. Get Total Count
    int totalCount = 0;
    await using (var countCmd = connection.CreateCommand())
    {
        countCmd.CommandText = $"SELECT COUNT(*) FROM QueueJobs {whereSql};";
        foreach (var p in parameters)
        {
            countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        }
        var countObj = await countCmd.ExecuteScalarAsync(ct);
        totalCount = Convert.ToInt32(countObj);
    }

    // 2. Fetch Paged Results
    var items = new List<QueueJob>();
    int offset = Math.Max(0, (filter.PageNumber - 1) * filter.PageSize);

    await using (var queryCmd = connection.CreateCommand())
    {
        queryCmd.CommandText = $@"
            SELECT * FROM QueueJobs 
            {whereSql}
            ORDER BY CreatedAt DESC
            LIMIT @PageSize OFFSET @Offset;";

        foreach (var p in parameters)
        {
            queryCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
        }
        queryCmd.Parameters.AddWithValue("@PageSize", filter.PageSize);
        queryCmd.Parameters.AddWithValue("@Offset", offset);

        await using var reader = await queryCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            items.Add(MapQueueJobFromReader(reader));
        }
    }

    return new PagedResult<QueueJob>
    {
        Items = items,
        TotalCount = totalCount,
        PageNumber = filter.PageNumber,
        PageSize = filter.PageSize
    };
}
```

---

### Scenario 8: Managing Scheduled & Recurring Jobs (`IScheduleJob`)

#### Fetching Due Scheduled Jobs (`GetDueJobsAsync`)

```csharp
public async Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync(DateTimeOffset now, CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    const string sql = @"
        SELECT * FROM ScheduledJobs
        WHERE IsEnabled = 1
          AND NextRunAt IS NOT NULL
          AND NextRunAt <= @Now
        ORDER BY NextRunAt ASC;";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@Now", now.ToString("O"));

    var results = new List<ScheduledJob>();
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        results.Add(MapScheduledJobFromReader(reader));
    }

    return results;
}
```

#### Recording Trigger Execution (`RecordTriggerAsync`)

Advances the next run time after a scheduled job generates a queue job:

```csharp
public async Task<bool> RecordTriggerAsync(
    Guid jobId, 
    DateTimeOffset triggeredAt, 
    DateTimeOffset? nextRunAt, 
    CancellationToken ct = default)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(ct);

    const string sql = @"
        UPDATE ScheduledJobs
        SET LastTriggeredAt = @TriggeredAt,
            NextRunAt = @NextRunAt,
            LastUpdate = @Now,
            UpdatedAt = @Now,
            IsEnabled = CASE WHEN @NextRunAt IS NULL THEN 0 ELSE IsEnabled END
        WHERE Id = @Id;";

    await using var cmd = connection.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@TriggeredAt", triggeredAt.ToString("O"));
    cmd.Parameters.AddWithValue("@NextRunAt", nextRunAt.HasValue ? (object)nextRunAt.Value.ToString("O") : DBNull.Value);
    cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
    cmd.Parameters.AddWithValue("@Id", jobId.ToString());

    int affected = await cmd.ExecuteNonQueryAsync(ct);
    return affected > 0;
}
```

---

## 7. Production Best Practices & Gotchas

### Handling `SQLITE_BUSY` and Concurrency Locks

Under extreme multi-threaded write load, SQLite may momentarily return error code `5` (`SQLITE_BUSY`). 

1. **Always configure `PRAGMA busy_timeout = 5000;`** so SQLite spins internally instead of failing instantly.
2. **Add a lightweight retry policy** for transient lock exceptions:

```csharp
public static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> action, int maxRetries = 3)
{
    for (int i = 0; i < maxRetries; i++)
    {
        try
        {
            return await action();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 5) // SQLITE_BUSY
        {
            if (i == maxRetries - 1) throw;
            await Task.Delay(Random.Shared.Next(10, 50));
        }
    }
    throw new InvalidOperationException("Execution retries exhausted.");
}
```

### Avoiding Data Reader Leaks with `await using`

Every `SqliteConnection`, `SqliteCommand`, and `SqliteDataReader` implements `IAsyncDisposable`. Failing to dispose a reader leaves the connection locked in reading state:

```csharp
// GOOD: Safe, garbage-collection friendly, releases SQLite locks immediately
await using var connection = new SqliteConnection(_connectionString);
await connection.OpenAsync(ct);
await using var cmd = connection.CreateCommand();
cmd.CommandText = "SELECT * FROM QueueJobs WHERE Id = @Id";
cmd.Parameters.AddWithValue("@Id", id.ToString());
await using var reader = await cmd.ExecuteReaderAsync(ct);
if (await reader.ReadAsync(ct))
{
    // ...
}
```

### Unit Testing with In-Memory SQLite

For fast unit tests without creating temporary files on disk:

```csharp
// Use shared in-memory database
var connectionString = "Data Source=InMemoryPicoQueue;Mode=Memory;Cache=Shared";

// Keep one connection open for the duration of the test run so the in-memory database is not destroyed
using var masterConnection = new SqliteConnection(connectionString);
masterConnection.Open();

// Your storage classes can now open and close connections using the same connection string
var storage = new SqliteJobQueue(connectionString);
await storage.InitializeAsync();
```

---

## 8. Reader Mapping Helper Methods

Keep reader mapping clean and centralized using helper methods:

```csharp
private static QueueJob MapQueueJobFromReader(SqliteDataReader reader)
{
    return new QueueJob
    {
        Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
        Name = reader.GetString(reader.GetOrdinal("Name")),
        Description = reader.IsDBNull(reader.GetOrdinal("Description")) 
            ? null 
            : reader.GetString(reader.GetOrdinal("Description")),
        TypeName = reader.GetString(reader.GetOrdinal("TypeName")),
        PayloadJson = reader.GetString(reader.GetOrdinal("PayloadJson")),
        Queue = reader.GetString(reader.GetOrdinal("Queue")),
        State = Enum.Parse<JobState>(reader.GetString(reader.GetOrdinal("State"))),
        Priority = reader.GetInt32(reader.GetOrdinal("Priority")),
        Attempt = reader.GetInt32(reader.GetOrdinal("Attempt")),
        MaxAttempts = reader.GetInt32(reader.GetOrdinal("MaxAttempts")),
        WorkerId = reader.IsDBNull(reader.GetOrdinal("WorkerId")) 
            ? null 
            : reader.GetString(reader.GetOrdinal("WorkerId")),
        ErrorMessage = reader.IsDBNull(reader.GetOrdinal("ErrorMessage")) 
            ? null 
            : reader.GetString(reader.GetOrdinal("ErrorMessage")),
        ErrorDetail = reader.IsDBNull(reader.GetOrdinal("ErrorDetail")) 
            ? null 
            : reader.GetString(reader.GetOrdinal("ErrorDetail")),
        StartedAt = ParseNullableDateTimeOffset(reader, "StartedAt"),
        CompletedAt = ParseNullableDateTimeOffset(reader, "CompletedAt"),
        HeartbeatAt = ParseNullableDateTimeOffset(reader, "HeartbeatAt"),
        RetryAt = ParseNullableDateTimeOffset(reader, "RetryAt"),
        CreatedAt = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("CreatedAt"))),
        LastUpdate = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("LastUpdate"))),
        ScheduledJobId = reader.IsDBNull(reader.GetOrdinal("ScheduledJobId"))
            ? null
            : Guid.Parse(reader.GetString(reader.GetOrdinal("ScheduledJobId")))
    };
}

private static DateTimeOffset? ParseNullableDateTimeOffset(SqliteDataReader reader, string column)
{
    int ordinal = reader.GetOrdinal(column);
    return reader.IsDBNull(ordinal) 
        ? null 
        : DateTimeOffset.Parse(reader.GetString(ordinal));
}
```
