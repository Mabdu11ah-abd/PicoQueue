# Implementation Plan: Section 3.1 — Job Definition & Enqueueing (FR1 – FR5)

## 1. Executive Summary & Architectural Clarification

Section 3.1 of the [Project Proposal](file:///c:/Repos/PicoQueue/Docs/project-proposal.md) defines the core developer contract for PicoQueue:
- **FR1:** Fire-and-forget enqueueing with a single method call.
- **FR2:** Delayed job scheduling for future execution.
- **FR3:** Recurring jobs defined by standard cron expressions.
- **FR4:** Strongly typed, serialized job payloads.
- **FR5:** Workload isolation and prioritization via queues and priority levels.

---

### Core Concept: Delayed Jobs are Scheduled Jobs; Scheduled Jobs Translate to QueueJobs

To prevent architectural confusion, the relationship between schedules and execution is formally clarified:

1. **Delayed Jobs (FR2) are simply One-Off Scheduled Jobs:**
   - There is no separate "DelayedJob" runtime entity.
   - A **delayed job** is a `ScheduledJob` where `IsRecurring = false` and `ScheduledAt` (or `NextRunAt`) is set to a specific future timestamp.
   - A **recurring job** is a `ScheduledJob` where `IsRecurring = true` and `CronExpression` defines its recurring intervals.

2. **Scheduled Jobs are NEVER Executed Directly:**
   - A `ScheduledJob` is purely a **schedule/trigger definition** (a template). It does not have states like `Processing`, `Retrying`, `Failed`, or `WorkerId`.
   - The worker ([JobProcessor](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Execution/JobProcessor.cs)) **never** dequeues or executes `ScheduledJob`s. Workers only know and execute `QueueJob`s.

3. **The Translation Pipeline (The Scheduler's Sole Responsibility):**
   - The [RecurringJobScheduler](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Scheduling/RecurringJobScheduler.cs) background service has exactly one responsibility:
     **Detect due `ScheduledJob`s (`NextRunAt <= UtcNow`) and translate them into standard `QueueJob` instances in the active queue.**
   - When translating, the scheduler must guarantee **Full Parameter Inheritance**:
     - `ScheduledJobId = scheduledJob.Id` (relational link to parent schedule)
     - `Queue = scheduledJob.Queue` (workload isolation)
     - `Priority = scheduledJob.Priority` (execution priority)
     - `MaxAttempts = scheduledJob.MaxAttempts` (retry limit)
     - `TypeName`, `PayloadJson`, `Name`, `Description` (job payload and metadata)
   - Once enqueued as a regular `QueueJob`, the worker picks it up from the queue and executes it identically to any fire-and-forget job.
   - For recurring jobs, the scheduler calculates and updates `NextRunAt` using the cron expression.
   - For one-off delayed jobs, the scheduler marks the schedule as triggered (`NextRunAt = null`, `IsEnabled = false`).

---

## 2. Architecture & Lifecycle Diagram

```
+--------------------------------------------------------------------------------------------------+
|                                        APPLICATION LAYER                                         |
|                                                                                                  |
|   1. Fire-and-Forget (FR1):                                                                      |
|      await queue.EnqueueAsync<SendEmailJob, EmailArgs>(args);                                    |
|                                                                                                  |
|   2. One-Off Delayed (FR2):                                                                      |
|      await scheduler.ScheduleAsync<SendEmailJob, EmailArgs>(args, runAt: in15Mins);              |
|                                                                                                  |
|   3. Recurring Cron (FR3):                                                                       |
|      await scheduler.RecurringAsync<ReportJob>("0 9 * * *", queue: "reports");                   |
+------------------------------------+------------------------------------+------------------------+
                                     |                                    |
                                     | (FR1: Direct Enqueue)              | (FR2 & FR3: Schedule)
                                     v                                    v
+---------------------------------------------------------+  +-------------------------------------+
|                  QUEUE STORAGE (Active)                 |  |       SCHEDULE STORAGE (Timers)     |
|                                                         |  |                                     |
|  QueueJobs Table                                        |  |  ScheduledJobs Table                |
|  -----------------------------------------------------  |  |  ---------------------------------  |
|  - Id                                                   |  |  - Id                               |
|  - TypeName, PayloadJson, Name, Description             |  |  - TypeName, PayloadJson, Name...   |
|  - Queue ("default", "emails", ...)                     |  |  - Queue ("default", "reports"...)  |
|  - Priority (10, 0, ...)                                |  |  - Priority, MaxAttempts            |
|  - MaxAttempts, Attempt, State, RetryAt...              |  |  - ScheduledAt, NextRunAt           |
|  - ScheduledJobId (FK to ScheduledJobs.Id if triggered) |  |  - IsRecurring (bool), CronExpr...  |
+----------------------------▲----------------------------+  +------------------+------------------+
                             |                                                  |
                             |                 TRANSLATION PIPELINE             |
                             |        [RecurringJobScheduler Background Service]|
                             +--------------------------------------------------+
                             |  1. Polls ScheduledJobs WHERE NextRunAt <= Now   |
                             |  2. Enqueues QueueJob with inherited parameters: |
                             |     (ScheduledJobId, Queue, Priority, MaxAttempts)
                             |  3. Updates schedule (NextRunAt or Complete)     |
                             |                                                  |
                             v
+--------------------------------------------------------------------------------------------------+
|                                    WORKER EXECUTION PIPELINE                                     |
|                                                                                                  |
|  [JobProcessor Background Service]                                                               |
|  - Dequeues next eligible QueueJob honoring configured Queue priority order                      |
|  - Deserializes typed payload using IJobSerializer                                               |
|  - Executes IJob / IJob<TArgs> within DI scope                                                   |
|  - Acknowledges success or schedules retry on failure                                            |
+--------------------------------------------------------------------------------------------------+
```

---

## 3. Issues & Root Cause Analysis

### 3.1. FR5 (Critical Defect): Worker Hardcoded to `"default"` Queue
- **Location:** [src/PicoQueue.Core/Execution/JobProcessor.cs#L54-L58](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Execution/JobProcessor.cs#L54-L58)
  ```csharp
  QueueJob? job = await _jobQueue.DequeueAsync(new DequeueJobDto
  {
      WorkerId = _workerId,
      Queue = "default" // <-- HARDCODED
  }, stoppingToken);
  ```
- **The Issue:** `JobProcessor` unconditionally asks for `"default"`. Any job enqueued directly or spawned by a schedule into a custom queue (e.g. `"emails"`, `"reports"`, `"critical"`) sits in SQLite unprocessed indefinitely.
- **Options Disconnect:** [PicoQueueOptions.cs#L36](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Options/PicoQueueOptions.cs#L36) defines `QueueName`, but it is completely ignored.
- **Single-Queue Constraint:** [DequeueJobDto.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/DTOs/Queue/DequeueJobDto.cs) only accepts a single `string Queue`. Workers cannot listen to multiple queues in priority order (e.g., `["critical", "default", "low"]`).
- **Storage Constraint:** [SqliteJobQueue.DequeueAsync](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/Services/SqliteJobQueue.cs#L78) only queries `WHERE Queue = @Queue`.
- **C# Model Defect:** In [QueueJob.cs#L8](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/QueueJob.cs#L8), `public int Priority;` is declared as a **public field** instead of an auto-property `public int Priority { get; set; } = 0;`.

---

### 3.2. FR1 & FR4: Poor Enqueue Ergonomics & Unimplemented `IJobSerializer`
- **Missing Serializer Implementation:** [IJobSerializer.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Serialization/IJobSerializer.cs) is defined in `PicoQueue.Abstractions`, but there is **no implementation** in `PicoQueue.Core` and no DI registration.
- **Bypassed Serializer in Worker:** [JobProcessor.cs#L128](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Execution/JobProcessor.cs#L128) invokes `JsonSerializer.Deserialize(job.PayloadJson, argType)` directly instead of using an injected `IJobSerializer`.
- **Raw DTO Ergonomics (FR1):** Callers currently must manually assemble `EnqueueJobDto`, provide `typeof(TJob).AssemblyQualifiedName!`, and serialize `JsonSerializer.Serialize(args)`.
- **Ignored Attribute:** [QueueAttribute.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Attributes/QueueAttribute.cs) exists (`[Queue("emails")]`), but nothing inspects it during enqueueing.

---

### 3.3. FR2 & FR3: Broken Parameter Inheritance in Translation Pipeline
- **Missing `ScheduledJobId` Linkage:**
  - [QueueJobs](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/schema/SchemaDefinitions.cs#L45) has a `ScheduledJobId` column, but [EnqueueJobDto.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/DTOs/Queue/EnqueueJobDto.cs) lacks `ScheduledJobId`.
  - [SqliteJobQueue.EnqueueAsync](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/Services/SqliteJobQueue.cs#L61-L67) never writes to `ScheduledJobId`.
  - When [RecurringJobScheduler.cs#L47-L56](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Scheduling/RecurringJobScheduler.cs#L47-L56) spawns a job from a schedule, it cannot pass `job.Id`, leaving the foreign key `NULL`.
- **Dropped Schedule Configuration (`Priority` & `MaxAttempts`):**
  - [CreateScheduledJobDto.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/DTOs/Scheduled/CreateScheduledJobDto.cs#L10-L11) accepts `Priority` and `MaxAttempts`.
  - However, [ScheduledJobs table](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/schema/SchemaDefinitions.cs#L59-L75) does NOT store `Priority` or `MaxAttempts`, and neither did [ScheduledJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/ScheduledJob.cs).
  - Consequently, [RecurringJobScheduler.cs#L54-L55](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Scheduling/RecurringJobScheduler.cs#L54-L55) hardcodes `Priority = 0` and `MaxAttempts = 3`, discarding whatever the developer configured on the schedule.

---

## 4. Target Data Model & Contracts

### 4.1. Unifying Job Configuration in `BaseJob`
Because every job definition (whether an immediate `QueueJob` or a scheduled trigger in `ScheduledJob`) has a target queue, execution priority, and retry ceiling, move `Priority` and `MaxAttempts` into [BaseJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/BaseJob.cs):

```csharp
public abstract class BaseJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Queue { get; set; } = "default";
    public int Priority { get; set; } = 0;
    public int MaxAttempts { get; set; } = 3;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.UtcNow;
}
```

- [QueueJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/QueueJob.cs) inherits `Priority` and `MaxAttempts` as properties, eliminating the public field bug (`public int Priority;`).
- [ScheduledJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/ScheduledJob.cs) inherits `Priority` and `MaxAttempts`, ensuring they are retained on the schedule.

### 4.2. DTO Updates in `PicoQueue.Abstractions`

1. **`EnqueueJobDto`**:
   ```csharp
   public record EnqueueJobDto
   {
       public required string TypeName { get; init; }
       public required string PayloadJson { get; init; }
       public string? Name { get; init; }
       public string? Description { get; init; }
       public string Queue { get; init; } = "default";
       public int Priority { get; init; } = 0;
       public int MaxAttempts { get; init; } = 3;
       public Guid? ScheduledJobId { get; init; } // Relational link back to schedule
   }
   ```

2. **`DequeueJobDto`**:
   ```csharp
   public record DequeueJobDto
   {
       public required string WorkerId { get; init; }
       public string Queue { get; init; } = "default";
       public IReadOnlyList<string>? Queues { get; init; } // Ordered list of queues
   }
   ```

---

## 5. Detailed Implementation Steps

### Phase 1: Core Models & Serialization Engine

#### Step 1.1: Update `BaseJob.cs` & `QueueJob.cs`
- In [BaseJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/BaseJob.cs): Add `public int Priority { get; set; } = 0;` and `public int MaxAttempts { get; set; } = 3;`.
- In [QueueJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Models/QueueJob.cs): Remove `public int Priority;` (now cleanly inherited as a property from `BaseJob`).

#### Step 1.2: Update DTOs
- In [EnqueueJobDto.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/DTOs/Queue/EnqueueJobDto.cs): Add `public Guid? ScheduledJobId { get; init; }`.
- In [DequeueJobDto.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/DTOs/Queue/DequeueJobDto.cs): Add `public IReadOnlyList<string>? Queues { get; init; }`.

#### Step 1.3: Implement `SystemTextJsonJobSerializer`
Create `src/PicoQueue.Core/Serialization/SystemTextJsonJobSerializer.cs` implementing [IJobSerializer](file:///c:/Repos/PicoQueue/src/PicoQueue.Abstractions/Serialization/IJobSerializer.cs):
- Configurable with `JsonSerializerOptions` (defaults to `PropertyNameCaseInsensitive = true`).
- Supports generic `Serialize<T>` / `Deserialize<T>` and non-generic `Serialize(object, Type)` / `Deserialize(string, Type)`.

#### Step 1.4: Register `IJobSerializer` in DI & Inject into `JobProcessor`
- In [PicoQueueCoreExtensions.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Extensions/PicoQueueCoreExtensions.cs):
  ```csharp
  services.TryAddSingleton<IJobSerializer, SystemTextJsonJobSerializer>();
  ```
- In [JobProcessor.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Execution/JobProcessor.cs): Inject `IJobSerializer` and replace `JsonSerializer.Deserialize` calls with `_serializer.Deserialize`.

---

### Phase 2: Ergonomic Enqueueing & Scheduling Extensions

#### Step 2.1: Implement `JobQueueExtensions` (FR1 & FR4)
Create `src/PicoQueue.Core/Extensions/JobQueueExtensions.cs`:
- `jobQueue.EnqueueAsync<TJob>(queue, priority, maxAttempts)`
- `jobQueue.EnqueueAsync<TJob, TArgs>(args, queue, priority, maxAttempts)`
- Automatic resolution of queue name:
  1. Explicit `queue` argument (if supplied).
  2. `[Queue("name")]` attribute on `TJob` class (if present).
  3. Default `"default"`.
- Automatic serialization of `TArgs` using `IJobSerializer`.

#### Step 2.2: Implement `ScheduleJobExtensions` (FR2 & FR3)
Create `src/PicoQueue.Core/Extensions/ScheduleJobExtensions.cs`:
- **Delayed Jobs (FR2):**
  - `scheduler.ScheduleAsync<TJob>(DateTimeOffset scheduledAt, ...)`
  - `scheduler.ScheduleAsync<TJob, TArgs>(TArgs args, DateTimeOffset scheduledAt, ...)`
  - `scheduler.ScheduleAsync<TJob>(TimeSpan delay, ...)`
  - `scheduler.ScheduleAsync<TJob, TArgs>(TArgs args, TimeSpan delay, ...)`
  *(Sets `IsRecurring = false`, `ScheduledAt = scheduledAt`)*
- **Recurring Jobs (FR3):**
  - `scheduler.RecurringAsync<TJob>(string cronExpression, ...)`
  - `scheduler.RecurringAsync<TJob, TArgs>(TArgs args, string cronExpression, ...)`
  *(Sets `IsRecurring = true`, `CronExpression = cronExpression`)*

---

### Phase 3: The Translation Pipeline & Multi-Queue Worker Dispatching

#### Step 3.1: Update `RecurringJobScheduler` Translation
In [RecurringJobScheduler.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Scheduling/RecurringJobScheduler.cs):
When processing due jobs, ensure **Full Parameter Inheritance** into `EnqueueJobDto`:
```csharp
await _jobQueue.EnqueueAsync(new EnqueueJobDto
{
    ScheduledJobId = job.Id,          // Linkage
    Name = job.Name,
    Description = job.Description,
    TypeName = job.TypeName,
    PayloadJson = job.PayloadJson,
    Queue = job.Queue,                // Preserved queue
    Priority = job.Priority,          // Preserved priority
    MaxAttempts = job.MaxAttempts     // Preserved max attempts
}, stoppingToken);

// For one-off delayed jobs, set nextRunAt to null and mark disabled
DateTimeOffset? nextRunAt = null;
if (job.IsRecurring && !string.IsNullOrWhiteSpace(job.CronExpression))
{
    var cron = CronExpression.Parse(job.CronExpression);
    nextRunAt = cron.GetNextOccurrence(now, TimeZoneInfo.Utc);
}

await _scheduleJob.RecordTriggerAsync(job.Id, now, nextRunAt, stoppingToken);

if (!job.IsRecurring)
{
    // One-off delayed job completed: disable so it doesn't trigger again
    await _scheduleJob.SetEnabledAsync(job.Id, false, stoppingToken);
}
```

#### Step 3.2: Update Worker Multi-Queue Configuration
In [PicoQueueOptions.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Options/PicoQueueOptions.cs):
Add `Queues` property:
```csharp
/// <summary>
/// Ordered list of queue names this worker instance listens to.
/// Jobs are dequeued honoring queue order first, then job priority.
/// Defaults to ["default"].
/// </summary>
public string[] Queues { get; set; } = ["default"];
```

In [JobProcessor.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Core/Execution/JobProcessor.cs):
Pass `Queues` to `DequeueJobDto`:
```csharp
var targetQueues = _options.Queues.Length > 0 ? _options.Queues : [_options.QueueName];
QueueJob? job = await _jobQueue.DequeueAsync(new DequeueJobDto
{
    WorkerId = _workerId,
    Queues = targetQueues
}, stoppingToken);
```

---

### Phase 4: SQLite Storage Updates (`PicoQueue.Storage.Sqlite`)

#### Step 4.1: Update Schema DDL
In [SchemaDefinitions.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/schema/SchemaDefinitions.cs):
Add `Priority INTEGER NOT NULL DEFAULT 0` and `MaxAttempts INTEGER NOT NULL DEFAULT 3` to `ScheduledJobs` table creation SQL.

#### Step 4.2: Persist `ScheduledJobId` in `SqliteJobQueue`
In [SqliteJobQueue.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/Services/SqliteJobQueue.cs):
1. In `EnqueueAsync`: Add `ScheduledJobId` to `INSERT INTO QueueJobs` and populate parameter from `dto.ScheduledJobId`.
2. In `DequeueAsync`: Support multi-queue matching:
   - Extract queue list: `var queues = (dto.Queues != null && dto.Queues.Count > 0) ? dto.Queues : [dto.Queue];`
   - Dynamically build SQL with parameterized queue list:
     ```sql
     SELECT Id FROM QueueJobs
     WHERE Queue IN (@q0, @q1, ...)
       AND (
           State = 'Enqueued'
           OR (State = 'Retrying' AND (RetryAt IS NULL OR RetryAt <= @Now))
       )
     ORDER BY
       CASE Queue
         WHEN @q0 THEN 0
         WHEN @q1 THEN 1
         ...
       END,
       Priority DESC,
       CreatedAt ASC
     LIMIT 1;
     ```

#### Step 4.3: Persist `Priority` & `MaxAttempts` in `SqliteScheduleJob`
In [SqliteScheduleJob.cs](file:///c:/Repos/PicoQueue/src/PicoQueue.Storage.Sqlite/Services/SqliteScheduleJob.cs):
- Save `Priority` and `MaxAttempts` in `INSERT INTO ScheduledJobs`.
- Read and map `Priority` and `MaxAttempts` in `GetDueJobsAsync`, `GetByIdAsync`, and `GetJobsAsync`.

---

## 6. Verification & Test Plan

1. **Multi-Queue Priority & Ordering Test:**
   - Enqueue jobs to `"critical"` (priority 0) and `"default"` (priority 10).
   - Dequeue with `Queues = ["critical", "default"]`.
   - Assert `"critical"` job is picked first despite lower numerical priority, validating strict queue ordering.
2. **Worker Custom Queue Execution Test:**
   - Configure `PicoQueueOptions.Queues = ["emails"]`.
   - Enqueue job to `"emails"`. Run `JobProcessor` and assert job runs and succeeds.
3. **Translation Pipeline & Parameter Inheritance Test:**
   - Schedule a recurring job with `Queue = "reports"`, `Priority = 5`, `MaxAttempts = 4`.
   - Trigger `RecurringJobScheduler`.
   - Inspect spawned `QueueJob`:
     - `ScheduledJobId == scheduledJob.Id`
     - `Queue == "reports"`
     - `Priority == 5`
     - `MaxAttempts == 4`
4. **Delayed Job Lifecycle Test:**
   - Schedule delayed job with `ScheduleAsync(delay: 50ms)`.
   - Run scheduler pass after delay. Assert `QueueJob` is enqueued and schedule is marked disabled (`IsEnabled == false`).
5. **Ergonomic Enqueueing & Serializer Test:**
   - Use `queue.EnqueueAsync<SendEmailJob, EmailArgs>(args)`.
   - Verify `[Queue]` attribute auto-discovery and typed argument deserialization.
6. **Full Solution Test Suite:**
   - Run `dotnet test` to confirm all 38 existing tests and new test fixtures pass.
