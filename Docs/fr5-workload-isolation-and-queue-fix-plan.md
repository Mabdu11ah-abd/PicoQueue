# Fix Plan: Workload Isolation via Queue Names & Priority (FR5) and Enqueueing Ergonomics (FR1, FR2, FR4)

## 1. Problem Definition & Context

During verification against the project proposal, a critical defect and three tightly coupled architectural gaps were identified in the job enqueuing and processing pipeline:

### 1.1 FR5 Defect (Critical): Hardcoded Queue Name in JobProcessor
In `src/PicoQueue.Core/Execution/JobProcessor.cs` (Line 57):
```csharp
QueueJob? job = await _jobQueue.DequeueAsync(new DequeueJobDto
{
    WorkerId = _workerId,
    Queue = "default"
}, stoppingToken);
```
- **The Defect:** `JobProcessor` unconditionally asks for `Queue = "default"`.
- **The Impact:** Any job enqueued to a custom queue (e.g., `"emails"`, `"reports"`, `"high-priority"`) is completely ignored by workers and will sit in the database unprocessed indefinitely.
- **Related Gap:** `PicoQueueOptions` has no configuration for which queue(s) a worker instance should listen to, nor any mechanism for queue prioritization or round-robin polling.

### 1.2 FR1 & FR4 Gaps: Low-Level Enqueue Ergonomics & Unwired `IJobSerializer`
- **FR1 Gap:** Enqueueing requires developers to manually construct `EnqueueJobDto`, assemble the assembly-qualified type name string, and serialize parameters to raw JSON manually.
- **FR4 Gap:** While `IJob<TArgs>` is supported in execution, `JobProcessor.cs` (Line 129) invokes `JsonSerializer.Deserialize` directly. The defined `IJobSerializer` interface in `PicoQueue.Abstractions` is not implemented in `PicoQueue.Core`, not registered in DI, and never used.

### 1.3 FR2 Gap: Missing `ScheduledJobId` Linkage
- When `RecurringJobScheduler` triggers a due scheduled job, it enqueues a `QueueJob` without passing `ScheduledJobId` because `EnqueueJobDto` lacks the property. The foreign key relation in the `QueueJobs` table remains `NULL`.

---

## 2. Proposed Architecture & Solution Design

```
+-----------------------------------------------------------------------------+
|                                Application                                  |
|                                                                             |
|   // FR1 & FR4: Ergonomic Enqueue Extensions                                |
|   await queue.EnqueueAsync<SendEmailJob, EmailArgs>(args, queue: "emails"); |
+------------------------------------+----------------------------------------+
                                     |
                                     v
+-----------------------------------------------------------------------------+
|                             PicoQueue.Core                                  |
|                                                                             |
|  - SystemTextJsonJobSerializer : IJobSerializer                             |
|  - PicoQueueOptions: Queues = ["critical", "default", "low"]                |
|  - JobProcessor: Multi-queue dequeueing with priority order                 |
|  - RecurringJobScheduler: Passes ScheduledJobId to EnqueueJobDto            |
+------------------------------------+----------------------------------------+
                                     |
                                     v
+-----------------------------------------------------------------------------+
|                         PicoQueue.Storage.Sqlite                            |
|                                                                             |
|  - DequeueJobDto: Supports IReadOnlyList<string> Queues                     |
|  - Atomic SQL query:                                                        |
|      WHERE Queue IN (...)                                                   |
|      ORDER BY CASE Queue ... END, Priority DESC, CreatedAt ASC              |
+-----------------------------------------------------------------------------+
```

---

## 3. Detailed Step-by-Step Implementation Plan

### Phase 1: Fix FR5 (Workload Isolation & Multi-Queue Processing)

#### Step 1.1: Update `PicoQueueOptions`
Add `Queues` property to `PicoQueueOptions` in `src/PicoQueue.Core/Options/PicoQueueOptions.cs`:
```csharp
/// <summary>
/// The ordered list of queue names this worker instance listens to.
/// Jobs are dequeued honoring queue order first, then job priority.
/// Defaults to ["default"].
/// </summary>
public string[] Queues { get; set; } = ["default"];
```

#### Step 1.2: Update `DequeueJobDto` in `PicoQueue.Abstractions`
Extend `DequeueJobDto` in `src/PicoQueue.Abstractions/DTOs/Queue/DequeueJobDto.cs`:
```csharp
public record DequeueJobDto
{
    public required string WorkerId { get; init; }
    public string Queue { get; init; } = "default";
    public IReadOnlyList<string>? Queues { get; init; }
}
```
*Backward-compatible: if `Queues` is null or empty, falls back to `[Queue]`.*

#### Step 1.3: Update `SqliteJobQueue.DequeueAsync`
Modify `src/PicoQueue.Storage.Sqlite/Services/SqliteJobQueue.cs`:
- Dynamically build the SQL `WHERE Queue IN (@q0, @q1, ...)` or queue priority ordering:
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
- This ensures that within the highest-priority queue, jobs are processed by `Priority DESC, CreatedAt ASC`. If that queue is empty, the next queue is seamlessly claimed in the same atomic transaction.

#### Step 1.4: Update `JobProcessor` to Pass Configured Queues
In `src/PicoQueue.Core/Execution/JobProcessor.cs`:
Pass `_options.Queues` to `DequeueAsync`:
```csharp
QueueJob? job = await _jobQueue.DequeueAsync(new DequeueJobDto
{
    WorkerId = _workerId,
    Queues = _options.Queues.Length > 0 ? _options.Queues : ["default"]
}, stoppingToken);
```

---

### Phase 2: Fix FR1 & FR4 (Enqueue Ergonomics & `IJobSerializer`)

#### Step 2.1: Implement `SystemTextJsonJobSerializer`
Create `src/PicoQueue.Core/Serialization/SystemTextJsonJobSerializer.cs` implementing `IJobSerializer`:
- Uses `System.Text.Json.JsonSerializerOptions` (e.g. `PropertyNameCaseInsensitive = true`).
- Supports generic and type-based serialization/deserialization.

#### Step 2.2: Register `IJobSerializer` in DI
In `src/PicoQueue.Core/Extensions/PicoQueueCoreExtensions.cs`:
```csharp
services.TryAddSingleton<IJobSerializer, SystemTextJsonJobSerializer>();
```

#### Step 2.3: Inject and Use `IJobSerializer` in `JobProcessor`
Replace raw `JsonSerializer.Deserialize` in `JobProcessor.cs` with the injected `IJobSerializer`.

#### Step 2.4: Create High-Level Enqueue Extension Methods (FR1)
Create `src/PicoQueue.Core/Extensions/JobQueueExtensions.cs`:
```csharp
public static class JobQueueExtensions
{
    public static Task<QueueJob> EnqueueAsync<TJob>(
        this IJobQueue jobQueue,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default) where TJob : IJob
    { ... }

    public static Task<QueueJob> EnqueueAsync<TJob, TArgs>(
        this IJobQueue jobQueue,
        TArgs args,
        IJobSerializer? serializer = null,
        string? queue = null,
        int priority = 0,
        int maxAttempts = 3,
        CancellationToken ct = default) where TJob : IJob<TArgs>
    { ... }
}
```

---

### Phase 3: Fix FR2 (`ScheduledJobId` Linkage)

#### Step 3.1: Add `ScheduledJobId` to `EnqueueJobDto`
In `src/PicoQueue.Abstractions/DTOs/Queue/EnqueueJobDto.cs`:
```csharp
public Guid? ScheduledJobId { get; init; }
```

#### Step 3.2: Persist `ScheduledJobId` in `SqliteJobQueue.EnqueueAsync`
Update the `INSERT INTO QueueJobs` statement in `SqliteJobQueue.cs` to include `ScheduledJobId`.

#### Step 3.3: Pass `ScheduledJobId` in `RecurringJobScheduler.cs`
In `RecurringJobScheduler.cs` (Line 48), map `ScheduledJobId = job.Id` so every job instance created from a schedule retains the reference to its parent `ScheduledJob`.

---

## 4. Verification & Testing Plan

### 4.1 Unit Tests
1. **Multi-Queue Priority Test (`SqliteJobQueueTests`):**
   - Enqueue jobs into `"critical"` (priority 0) and `"default"` (priority 10).
   - Dequeue with `Queues = ["critical", "default"]`.
   - Verify the `"critical"` queue job is picked up first despite lower priority number, validating strict queue ordering.
2. **Worker Custom Queue Processing Test (`JobProcessorTests`):**
   - Configure `PicoQueueOptions.Queues = ["emails"]`.
   - Enqueue a job into `Queue = "emails"`.
   - Run `JobProcessor` and assert job is dequeued, executed, and completed.
3. **`IJobSerializer` & High-Level Enqueue Test (`JobQueueExtensionsTests`):**
   - Use `queue.EnqueueAsync<MyJob, MyArgs>(args)`.
   - Verify payload is properly serialized using `IJobSerializer` and enqueued.
4. **`ScheduledJobId` Linkage Test (`RecurringJobSchedulerTests`):**
   - Trigger a due scheduled job.
   - Assert the spawned `QueueJob` contains `ScheduledJobId == scheduledJob.Id`.

### 4.2 Integration & Regression Testing
- Run all 38 existing tests (`dotnet test`) to verify zero regressions.
- Update `RealisticProgramE2ETests` to demonstrate ergonomic enqueuing with custom queues.
