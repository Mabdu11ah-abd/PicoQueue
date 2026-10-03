# PicoQueue.Abstractions — Architecture & Contracts

`PicoQueue.Abstractions` defines the decoupled contracts, data transfer objects, attributes, and models that govern PicoQueue. It carries **zero third-party dependencies** and serves as the common interface layer shared by the core execution engine, storage implementations, and client applications.

---

## 1. Core Interfaces

### `IJob` & `IJob<TArgs>`
The job authoring contracts implemented by end-user jobs:

- **`IJob`**: Fire-and-forget job without arguments.
  ```csharp
  public interface IJob
  {
      Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken);
  }
  ```
- **`IJob<TArgs>`**: Strongly-typed job accepting serialized payload arguments.
  ```csharp
  public interface IJob<in TArgs>
  {
      Task ExecuteAsync(TArgs args, JobExecutionContext context, CancellationToken cancellationToken);
  }
  ```

---

### `IJobQueue`
The primary storage contract for managing immediate queue jobs throughout their execution lifecycle:

```csharp
public interface IJobQueue
{
    // Enqueue & Dequeue
    Task<QueueJob> EnqueueAsync(EnqueueJobDto dto, CancellationToken ct = default);
    Task<QueueJob?> DequeueAsync(DequeueJobDto dto, CancellationToken ct = default);

    // Query & Fetch
    Task<QueueJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);
    Task<PagedResult<QueueJob>> GetJobsAsync(GetJobsFilterDto filter, CancellationToken ct = default);

    // State Transitions & Lifecycle
    Task<bool> AcknowledgeSuccessAsync(Guid jobId, CancellationToken ct = default);
    Task<bool> AcknowledgeFailureAsync(Guid jobId, string errorMessage, string? errorDetail = null, DateTimeOffset? retryAt = null, CancellationToken ct = default);
    Task<bool> RetryJobAsync(RetryJobDto dto, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(Guid jobId, CancellationToken ct = default);

    // Worker Liveness & Recovery
    Task<bool> UpdateHeartbeatAsync(Guid jobId, CancellationToken ct = default);
    Task<int> RecoverOrphanedJobsAsync(TimeSpan heartbeatTimeout, CancellationToken ct = default);

    // Retention Cleanup
    Task<int> PurgeFinishedJobsAsync(TimeSpan olderThan, CancellationToken ct = default);
}
```

---

### `IScheduleJob`
The scheduling storage contract for delayed and recurring jobs:

```csharp
public interface IScheduleJob
{
    Task<ScheduledJob> ScheduleAsync(ScheduleJobDto dto, CancellationToken ct = default);
    Task<ScheduledJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default);
    Task<PagedResult<ScheduledJob>> GetJobsAsync(GetScheduledJobsFilterDto filter, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync(DateTimeOffset asOf, int limit = 100, CancellationToken ct = default);
    Task<bool> RecordTriggerAsync(Guid jobId, DateTimeOffset triggeredAt, DateTimeOffset? nextRunAt, CancellationToken ct = default);
    Task<bool> SetEnabledAsync(Guid jobId, bool isEnabled, CancellationToken ct = default);
    Task<bool> DeleteJobAsync(Guid jobId, CancellationToken ct = default);
}
```

---

## 2. Models & Enums

### `JobState`
Defines the state machine for a queued job:

```csharp
public enum JobState
{
    Enqueued,    // Waiting in queue for an available worker
    Processing,  // Currently claimed and executing on a worker
    Succeeded,   // Completed successfully
    Failed,      // Failed and exhausted all retry attempts
    Retrying,    // Failed an attempt; waiting for scheduled backoff retry
    Deleted,     // Manually cancelled or marked as deleted
    Expired      // Expired before execution
}
```

### Domain Models
- **`BaseJob`**: Base record containing common identifiers (`Id`, `Name`, `Description`, `TypeName`, `PayloadJson`, `Queue`, `CreatedAt`, `LastUpdate`).
- **`QueueJob`**: Extends `BaseJob` with execution state (`State`, `Priority`, `Attempt`, `MaxAttempts`, `WorkerId`, `HeartbeatAt`, `StartedAt`, `CompletedAt`, `RetryAt`, `ErrorMessage`, `ErrorDetail`, `ScheduledJobId`).
- **`ScheduledJob`**: Extends `BaseJob` with scheduling metadata (`ScheduledAt`, `IsRecurring`, `CronExpression`, `NextRunAt`, `LastTriggeredAt`, `IsEnabled`).

---

## 3. Attributes

### `[Queue("name")]`
Specifies the target queue name for a job class:

```csharp
[Queue("emails")]
public class SendWelcomeEmailJob : IJob<SendWelcomeEmailArgs>
{
    public Task ExecuteAsync(SendWelcomeEmailArgs args, JobExecutionContext context, CancellationToken cancellationToken)
    {
        // Executes within "emails" queue
        return Task.CompletedTask;
    }
}
```

The static helper `QueueAttribute.GetQueueName(Type jobType, string fallback = "default")` extracts the queue name automatically via reflection.

---

## 4. Execution Context & DTOs

### `JobExecutionContext`
Ambient execution context supplied to `ExecuteAsync`:
- `JobId`: Unique job identifier.
- `Queue`: Queue name.
- `AttemptNumber`: Current execution attempt (1-based).
- `MaxAttempts`: Configured attempt threshold.
- `CreatedAt`: Original enqueue timestamp.
- `ScheduledJobId`: Optional ID linking back to the originating `ScheduledJob`.

### Data Transfer Objects
- **`EnqueueJobDto`**: Input for queueing a job (`TypeName`, `PayloadJson`, `Name`, `Queue`, `Priority`, `MaxAttempts`, `ScheduledJobId`).
- **`DequeueJobDto`**: Parameters for claiming a job (`WorkerId`, `Queue` filter).
- **`RetryJobDto`**: Manual retry request (`JobId`, `ResetAttemptCount`).
- **`ScheduleJobDto`**: Schedule parameters (`CronExpression`, `ScheduledAt`, `IsRecurring`).
- **`GetJobsFilterDto` & `PagedResult<T>`**: Paginated querying by `Queue` and `JobState`.
