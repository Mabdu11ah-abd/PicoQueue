# PicoQueue.Core Implementation Plan

This plan outlines the architecture and implementation steps for building the `PicoQueue.Core` engine. This engine acts as the "brain" of PicoQueue, sitting between the `Abstractions` and the underlying `Storage` backend. It's an excellent training exercise to implement these components step-by-step.

## Open Questions & Considerations

1. **Cron Expression Parsing:** A robust cron parser is required for recurring jobs. We recommend using the [Cronos](https://github.com/HangfireIO/Cronos) NuGet package (maintained by the Hangfire team). It's lightweight and thoroughly tested.
2. **Dependency Injection:** How should we handle DI for user-defined jobs? A standard approach is to require users to register their job classes (e.g., as `Transient` or `Scoped`) in the ASP.NET Core `IServiceCollection`. The core engine will use an `IServiceScopeFactory` to resolve them just before execution.
3. **Polling vs. Signaling:** Since we want to avoid heavy polling, consider implementing an in-memory `Channel<T>` for immediate execution of newly enqueued jobs on the same node, falling back to database polling only as a safety net.
4. **Stalled / Orphaned Jobs:** We need a strategy for jobs that crash mid-execution (e.g., power loss). A Heartbeat monitor running every few minutes to transition stalled jobs back to `Enqueued` is a reliable pattern.

---

## Folder Structure

When building the `PicoQueue.Core` project, organize the files as follows to maintain clear domain boundaries:

```text
PicoQueue.Core/
├── PicoQueue.Core.csproj
├── Options/
│   └── PicoQueueOptions.cs
├── Execution/
│   ├── IJobFactory.cs
│   ├── DefaultJobFactory.cs
│   └── JobProcessor.cs
├── Scheduling/
│   └── RecurringJobScheduler.cs
├── Retry/
│   ├── IRetryPolicy.cs
│   └── DefaultRetryPolicy.cs
├── Monitoring/
│   └── HeartbeatMonitor.cs
└── Extensions/
    └── PicoQueueCoreExtensions.cs
```

---

## Proposed Architecture & Components

We will build the core engine out of several decoupled components. By following this guide, you can incrementally build out `PicoQueue.Core`.

### 1. Configuration & Options
Define how the user configures the engine (e.g., worker count, polling intervals).

- **`PicoQueueOptions.cs`**
  - `MaxConcurrentJobs`: Limits how many jobs can run simultaneously (e.g., default `Environment.ProcessorCount * 2`).
  - `QueuePollingInterval`: How often to poll the DB if the in-memory queue is empty (e.g., 5 seconds).
  - `SchedulePollingInterval`: How often to check for due recurring jobs (usually 1 minute).
  - `HeartbeatMonitorInterval`: How often the system checks for orphaned jobs.

### 2. Execution Pipeline
The background service that fetches jobs from storage, instantiates them, and runs them.

- **`JobProcessor.cs` (Inherits `BackgroundService`)**
  - Uses `SemaphoreSlim` based on `MaxConcurrentJobs` to throttle execution concurrency.
  - In a continuous loop, calls `IJobQueue.DequeueAsync`.
  - Upon receiving a job, creates an `IServiceScope` and resolves the `IJob` instance using the `IJobFactory`.
  - Executes the job inside a `try/catch` block.
  - Calls `AcknowledgeSuccessAsync` on success, or `AcknowledgeFailureAsync` on failure.

- **`IJobFactory.cs` & `DefaultJobFactory.cs`**
  - Abstracts the creation of job instances.
  - The default implementation should use `ActivatorUtilities.CreateInstance(serviceProvider, jobType)` to properly inject dependencies into the job's constructor.

### 3. Retry Engine
Calculates backoff times for failed jobs to prevent hammering failing external services.

- **`IRetryPolicy.cs` & `DefaultRetryPolicy.cs`**
  - Implements **exponential backoff with jitter** (e.g., `Delay = Base * 2^(Attempt-1) + RandomJitter`).
  - Provides a method `ShouldRetry(attempt, maxAttempts)` to dictate whether a job should be retried or marked permanently failed.
  - The `JobProcessor` utilizes this policy in its `catch` block to schedule the `NextRunAt` timestamp.

### 4. Recurring Job Scheduler
The background loop that triggers scheduled and cron jobs.

- **`RecurringJobScheduler.cs` (Inherits `BackgroundService`)**
  - Wakes up periodically (e.g., every minute, aligned to the 00 second mark).
  - Calls `IScheduleJob.GetDueJobsAsync`.
  - For each due job, it enqueues a new queue execution record into `IJobQueue`.
  - Uses the `Cronos` library to calculate the next cron occurrence and updates the recurring job state via `RecordTriggerAsync`.

### 5. Heartbeat & Orphan Recovery
Ensures the system recovers if the host process crashes while a job is in the `Processing` state.

- **`HeartbeatMonitor.cs` (Inherits `BackgroundService`)**
  - Periodically calls `UpdateHeartbeatAsync` for all currently executing jobs on the local node to prove liveness.
  - Detects jobs in the database whose heartbeat hasn't been updated in a specified timeout (e.g., 5 minutes) and transitions them back to `Enqueued`.

### 6. Dependency Injection Setup
Wire it all together for the consumer.

- **`PicoQueueCoreExtensions.cs`**
  - Extension methods for `IServiceCollection` to register `JobProcessor`, `RecurringJobScheduler`, and `HeartbeatMonitor` using `services.AddHostedService<T>()`.
  - Registers the default options, `IRetryPolicy`, and `IJobFactory`.

---

## Build & Publishing Plan

To ensure the library is properly packaged and distributable:

1. **Project Configuration (`PicoQueue.Core.csproj`)**
   - Ensure the project targets `.net10.0`.
   - Set `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>` in the final publishing step.
   - Include package metadata (`<Description>`, `<Version>`, `<Authors>`, `<PackageLicenseExpression>`, etc.).
   
2. **Dependencies**
   - Add a `PackageReference` for `Cronos`.
   - Add `ProjectReference` to `PicoQueue.Abstractions`.
   - Add `PackageReference` to `Microsoft.Extensions.Hosting.Abstractions` and `Microsoft.Extensions.DependencyInjection.Abstractions`.

3. **CI/CD Pipeline (GitHub Actions)**
   - Create a workflow (e.g., `.github/workflows/build.yml`) to trigger on pushes to `main`.
   - Run `dotnet test` across the solution.
   - Run `dotnet pack -c Release` and push the `.nupkg` to NuGet.org.

---

## Verification & Testing Plan

As you build out these components, verify them using the following approaches:

### Automated Unit Tests (using xUnit & Moq)
- **Retry Math:** Unit tests for `DefaultRetryPolicy` ensuring exponential calculations and jitter boundaries are mathematically correct.
- **Scheduler Logic:** Unit tests for `RecurringJobScheduler` verifying it properly parses cron expressions and calculates the correct next run times.

### Integration / Manual Verification
- Write a simple ASP.NET Core Web API or Console App.
- Enqueue a sample `EmailJob` that logs to the console and throws exceptions on purpose to test the retry engine.
- Verify that the `JobProcessor` properly throttles concurrency if you rapidly enqueue 100 long-running jobs.
