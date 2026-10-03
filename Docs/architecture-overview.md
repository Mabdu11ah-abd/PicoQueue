# PicoQueue — Architecture Overview & Integration Guide

Welcome to the architectural guide for **PicoQueue**, an embeddable, zero-infrastructure background job queue for .NET.

This document outlines the system architecture and serves as the master index connecting the modular packages of the PicoQueue ecosystem:

- 📑 **[Abstractions Documentation](file:///c:/Repos/PicoQueue/Docs/abstractions.md)**: Public contracts, models, DTOs, and attributes.
- ⚙️ **[Core Engine Documentation](file:///c:/Repos/PicoQueue/Docs/core.md)**: Concurrency workers, dispatchers, scheduling, and retry policies.
- 💾 **[SQLite Storage Documentation](file:///c:/Repos/PicoQueue/Docs/storage-sqlite.md)**: Raw ADO.NET persistence, schema, indexes, and locking transactions.

---

## 1. High-Level Component Architecture

PicoQueue follows a strictly layered, dependency-inverted design:

```mermaid
graph TD
    subgraph Client Application
        App["Web API / Worker Service"]
        Jobs["Job Implementations (IJob, IJob&lt;T&gt;)"]
    end

    subgraph "PicoQueue.Abstractions (Zero Dependencies)"
        IJob["IJob / IJob&lt;T&gt;"]
        IJobQueue["IJobQueue Contract"]
        IScheduleJob["IScheduleJob Contract"]
        Models["QueueJob / ScheduledJob / JobState"]
    end

    subgraph "PicoQueue.Core (Execution Engine)"
        JP["JobProcessor Worker"]
        RJS["RecurringJobScheduler Worker"]
        HM["HeartbeatMonitor Worker"]
        RPS["RetentionPurgeService Worker"]
        Retry["IRetryPolicy Engine"]
    end

    subgraph "PicoQueue.Storage.Sqlite (Persistence)"
        SqliteQ["SqliteJobQueue"]
        SqliteSched["SqliteScheduleJob"]
        Init["SqliteDbInitializer (WAL Mode)"]
        DB[(picoqueue.db)]
    end

    %% Dependencies
    App --> IJob
    Jobs --> IJob
    App --> PicoQueue.Core
    PicoQueue.Core --> PicoQueue.Abstractions
    PicoQueue.Storage.Sqlite --> PicoQueue.Abstractions

    %% Runtime Wiring
    JP -.->|Dequeue / Ack| IJobQueue
    HM -.->|Recover Orphans| IJobQueue
    RPS -.->|Purge Finished| IJobQueue
    RJS -.->|Fetch Due & Enqueue| IScheduleJob
    RJS -.->|Enqueue Due Jobs| IJobQueue

    SqliteQ -.->|Implements| IJobQueue
    SqliteSched -.->|Implements| IScheduleJob
    SqliteQ --> DB
    SqliteSched --> DB
```

---

## 2. End-to-End Job Lifecycle

```mermaid
sequenceDiagram
    autonumber
    actor App as Application Code
    participant CoreExt as Core Extensions
    participant Storage as SqliteJobQueue
    participant Worker as JobProcessor
    participant Scope as IServiceProvider Scope
    participant Job as IJob Instance
    participant Monitor as HeartbeatMonitor
    participant Purger as RetentionPurgeService

    %% Enqueue
    App->>CoreExt: EnqueueAsync<SendEmailJob>(args)
    CoreExt->>Storage: EnqueueAsync(dto)
    Storage-->>App: Returns QueueJob (State = Enqueued)

    %% Dispatch & Lock
    Worker->>Storage: DequeueAsync(WorkerId, Queues)
    Note over Storage: BEGIN IMMEDIATE lock<br/>Select & Update to Processing
    Storage-->>Worker: Dispatched QueueJob

    %% Execution & Heartbeat
    Worker->>Scope: Create Scope & Resolve SendEmailJob
    par Job Execution
        Worker->>Job: ExecuteAsync(context, ct)
    and Heartbeat Loop
        Worker->>Storage: UpdateHeartbeatAsync(jobId) every 15s
    end

    %% Completion / Failure
    alt Successful Execution
        Job-->>Worker: Completed
        Worker->>Storage: AcknowledgeSuccessAsync(jobId)
        Note over Storage: State = Succeeded
    else Unhandled Exception
        Job-->>Worker: Throws Exception
        Worker->>Storage: AcknowledgeFailureAsync(jobId, retryAt)
        Note over Storage: State = Retrying (if attempts left)<br/>or State = Failed
    else Worker Crash / Power Loss
        Note over Worker: Worker abruptly dies
        Monitor->>Storage: RecoverOrphanedJobsAsync(timeout)
        Note over Storage: Heartbeat timed out<br/>Increment Attempt -> Retrying/Failed
    end

    %% Purge
    Note over Purger: Periodic Timer (e.g. 1 hour)
    Purger->>Storage: PurgeFinishedJobsAsync(RetentionPeriod)
    Note over Storage: Deletes Succeeded/Failed/Deleted<br/>older than 7 days
```

---

## 3. End-to-End Implementation Example

### A. Job Definition
Implement `IJob<TArgs>` and optionally annotate with `[Queue("name")]`:

```csharp
using PicoQueue.Abstractions.Attributes;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.Interfaces;

namespace SampleApp.Jobs;

public record GenerateReportArgs(string ReportType, string UserEmail);

[Queue("reports")]
public class GenerateReportJob : IJob<GenerateReportArgs>
{
    private readonly ILogger<GenerateReportJob> _logger;

    public GenerateReportJob(ILogger<GenerateReportJob> logger)
    {
        _logger = logger;
    }

    public async Task ExecuteAsync(
        GenerateReportArgs args,
        JobExecutionContext context,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Generating {Type} for {Email} (Attempt {Attempt}/{Max})",
            args.ReportType, args.UserEmail, context.AttemptNumber, context.MaxAttempts);

        // Do background work with cooperative cancellation
        await Task.Delay(2000, cancellationToken);
    }
}
```

---

### B. Startup Configuration (`Program.cs`)
Combine `AddPicoQueueCore` with `AddPicoQueueSqlite`:

```csharp
using PicoQueue.Core.Extensions;
using PicoQueue.Storage.Sqlite.Extensions;
using SampleApp.Jobs;

var builder = WebApplication.CreateBuilder(args);

// 1. Register Core Engine & Background Hosted Services
builder.Services.AddPicoQueueCore(options =>
{
    options.MaxConcurrentJobs = 8;
    options.QueuePollingInterval = TimeSpan.FromSeconds(2);
    options.RetentionPeriod = TimeSpan.FromDays(14); // Keep history for 14 days
});

// 2. Register SQLite Storage Backend
builder.Services.AddPicoQueueSqlite("Data Source=sample_queue.db;Cache=Default");

var app = builder.Build();
```

---

### C. Enqueueing & Scheduling from Endpoints
Inject `IJobQueue` and `IScheduleJob` wherever needed:

```csharp
// Fire-and-forget job
app.MapPost("/reports", async (IJobQueue queue, GenerateReportArgs req) =>
{
    Guid jobId = await queue.EnqueueAsync<GenerateReportJob, GenerateReportArgs>(req);
    return Results.Accepted($"/jobs/{jobId}", new { JobId = jobId });
});

// Delayed execution (runs 3 hours from now)
app.MapPost("/reports/delayed", async (IScheduleJob scheduler, GenerateReportArgs req) =>
{
    Guid scheduledId = await scheduler.ScheduleAsync<GenerateReportJob>(
        DateTimeOffset.UtcNow.AddHours(3),
        name: "Delayed 3h Report");

    return Results.Accepted($"/scheduled/{scheduledId}");
});

// Recurring Cron execution (runs every midnight UTC)
app.MapPost("/reports/recurring", async (IScheduleJob scheduler) =>
{
    Guid scheduledId = await scheduler.ScheduleAsync<GenerateReportJob>(
        cronExpression: "0 0 * * *",
        name: "Daily Midnight Report");

    return Results.Ok(new { ScheduledId = scheduledId });
});

app.Run();
```

---

## 4. Swapping Storage Backends

Because `PicoQueue.Core` only depends on `IJobQueue` and `IScheduleJob`, alternative storage engines (e.g., PostgreSQL or SQL Server) can be built without changing core engine code:

```csharp
// From SQLite:
builder.Services.AddPicoQueueSqlite(sqliteConnectionString);

// To PostgreSQL (Separate package):
// builder.Services.AddPicoQueuePostgres(pgConnectionString);
```

The application's job definitions, enqueue invocations, retry mechanics, concurrency throttling, and scheduling logic remain identical.
