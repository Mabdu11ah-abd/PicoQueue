# Adding a Storage Backend

PicoQueue is designed from the ground up to be storage-agnostic. While SQLite is the default zero-config provider, you can easily implement support for PostgreSQL, SQL Server, MySQL, MongoDB, Redis, or any other data store by implementing the `IJobStorage` interface.

This guide walks you through building, testing, and packaging a new storage provider.

---

## 1. The `IJobStorage` Interface

All storage providers implement the `IJobStorage` interface located in `PicoQueue.Abstractions`:

```csharp
namespace PicoQueue.Abstractions;

public interface IJobStorage
{
    /// <summary>
    /// Initializes schema, tables, and indexes if they do not already exist.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues a new job to storage.
    /// </summary>
    Task<string> EnqueueAsync(JobRecord job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically fetches and locks the next due job for execution.
    /// </summary>
    Task<JobRecord?> FetchNextDueJobAsync(string queueName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the state, retry attempt count, and metadata for a job.
    /// </summary>
    Task UpdateJobStateAsync(
        string jobId,
        JobState newState,
        string? errorDetails = null,
        DateTimeOffset? nextRunUtc = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the heartbeat timestamp for an actively running job.
    /// </summary>
    Task HeartbeatAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single job by its unique identifier.
    /// </summary>
    Task<JobRecord?> GetJobByIdAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries jobs by state, queue, or pagination criteria (used by Dashboard and APIs).
    /// </summary>
    Task<PagedResult<JobRecord>> QueryJobsAsync(JobQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks a job as deleted or purges it from the database.
    /// </summary>
    Task<bool> DeleteJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues jobs that were left in Processing state with expired heartbeats.
    /// </summary>
    Task<int> RecoverOrphanedJobsAsync(TimeSpan heartbeatTimeout, CancellationToken cancellationToken = default);
}
```

---

## 2. Core Implementation Requirements

### Atomic Job Pickup
When multiple worker processes or instances are running concurrently, `FetchNextDueJobAsync` must ensure that each job is claimed by **exactly one** worker to prevent duplicate execution.

- **PostgreSQL:** Use `FOR UPDATE SKIP LOCKED`:
  ```sql
  SELECT * FROM picoqueue_jobs
  WHERE queue_name = @Queue AND state = 'Enqueued' AND run_at <= @Now
  ORDER BY priority DESC, run_at ASC
  FOR UPDATE SKIP LOCKED
  LIMIT 1;
  ```
- **SQL Server:** Use table hints:
  ```sql
  SELECT TOP 1 * FROM PicoQueueJobs WITH (ROWLOCK, READPAST, UPDLOCK)
  WHERE QueueName = @Queue AND State = 'Enqueued' AND RunAt <= @Now
  ORDER BY Priority DESC, RunAt ASC;
  ```
- **SQLite:** Handled via transaction locks (e.g. `BEGIN IMMEDIATE` transaction) and `UPDATE ... SET State = 'Processing'`.

### State Consistency
Always ensure state transitions adhere to the standard lifecycle:
- `Enqueued` ➔ `Processing`
- `Processing` ➔ `Succeeded`, `Retrying`, or `Failed`
- Any state ➔ `Deleted`

### Heartbeats & Orphaned Job Recovery
If an application server crashes while a job is `Processing`, the storage provider must allow `RecoverOrphanedJobsAsync` to find jobs whose `HeartbeatUtc` is older than the timeout and transition them back to `Retrying` or `Enqueued`.

---

## 3. Dependency Injection Integration

Provide an extension method on `PicoQueueOptions` or `IServiceCollection` to register your backend cleanly:

```csharp
public static class PostgresJobStorageExtensions
{
    public static PicoQueueOptions UsePostgres(this PicoQueueOptions options, string connectionString)
    {
        options.Services.AddSingleton<IJobStorage>(sp => 
            new PostgresJobStorage(connectionString, sp.GetRequiredService<ILogger<PostgresJobStorage>>()));
        return options;
    }
}
```

Usage in user applications:
```csharp
builder.Services.AddPicoQueue(options =>
{
    options.UsePostgres(builder.Configuration.GetConnectionString("DefaultConnection")!);
});
```

---

## 4. Testing Your Storage Provider

PicoQueue includes a shared test suite in `PicoQueue.Tests` that verifies your storage implementation against all contract requirements:

1. Create a test project `PicoQueue.Storage.<YourBackend>.Tests`.
2. Reference the shared test fixture `JobStorageTestSuite<TStorage>`.
3. Run `dotnet test` to automatically validate:
   - Enqueue and retrieval semantics
   - Concurrency and lock contention (no double fetches)
   - State transitions and retry backoff updates
   - Orphaned job heartbeat recovery
   - Pagination and query filters

---

## 5. Submitting to the Community

1. Follow the guidelines in [`CONTRIBUTING.md`](../CONTRIBUTING.md).
2. Ensure your package uses modern C# / .NET 8+ and includes XML documentation comments.
3. Open a pull request targeting the `main` branch with your storage provider and its integration test suite!
