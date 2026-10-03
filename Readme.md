# PicoQueue

**A zero-config, no-ORM, SQLite-first background job queue for .NET.**

Install the package, and a persistent job queue is running against a local SQLite file — no database server, no schema migrations, no Entity Framework, no configuration required to get started. When you outgrow that, swap in Postgres or SQL Server through the same interface, without changing any job code.

```csharp
builder.Services.AddPicoQueue(); // that's it — a persistent queue is now running

app.MapPost("/signup", (JobQueue queue, SignupRequest req) =>
{
    queue.Enqueue(() => emailSender.SendWelcomeEmail(req.Email));
    return Results.Ok();
});
```

---

## Why

Most .NET background job libraries assume you're ready to stand up infrastructure before you write your first job:

- **Hangfire** is mature and full-featured, but requires SQL Server or Redis from day one, and some capabilities (batching at scale, priority queues, certain dashboard features) sit behind a commercial **Hangfire.Pro** license.
- **Quartz.NET** is an excellent, battle-tested scheduler, but ships with no dashboard and is oriented toward enterprise scheduling complexity (calendars, misfire policies) most apps don't need.
- **TickerQ** is a modern, actively developed alternative with a live dashboard — but it's built on Entity Framework Core, which means a `DbContext`, migrations, and an ORM dependency even if all you want is a local file.
- **Coravel** is genuinely zero-config, but it's in-memory only — a restart or crash silently loses everything queued.

There's no well-established option that treats **a local SQLite file with zero setup** as the actual default, with no ORM in the way, while still surviving restarts and giving you a dashboard, retries, and scheduling out of the box.

PicoQueue fills that specific gap. It isn't trying to replace Hangfire for large, distributed, high-throughput systems — it's for the much more common case: a single app (or a handful of instances) that needs reliable background jobs without an infrastructure decision attached.

## Features

- ✅ **Zero-config startup** — one line of DI registration, no database setup
- ✅ **No ORM dependency** — built directly on `Microsoft.Data.Sqlite`, not Entity Framework Core
- ✅ **Persistent by default** — jobs survive application restarts and crashes
- ✅ **Automatic retries** with configurable exponential backoff
- ✅ **Delayed & recurring jobs** — schedule for later, or use standard cron expressions
- ✅ **Pluggable storage** — swap SQLite for Postgres or SQL Server via a documented `IJobStorage` interface, without changing job code
- ✅ **Optional dashboard** — a separate package, so production deployments that don't need a UI aren't forced to carry its dependencies
- ✅ **MIT licensed**, no paid tier, no feature paywall

## Getting started

### Install

```bash
dotnet add package PicoQueue.Core
dotnet add package PicoQueue.AspNetCore
```

### Register the queue

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddPicoQueue(); // creates ./picoqueue.db automatically if it doesn't exist

var app = builder.Build();
```

### Enqueue a job

```csharp
app.MapPost("/reports", (JobQueue queue) =>
{
    queue.Enqueue(() => reportGenerator.Generate());
    return Results.Accepted();
});
```

### Schedule a delayed job

```csharp
queue.Schedule(() => reminderService.SendReminder(userId), TimeSpan.FromHours(24));
```

### Schedule a recurring job

```csharp
queue.ScheduleRecurring("nightly-cleanup", "0 0 * * *", () => cleanupService.Run());
```

### Add the dashboard (optional)

```bash
dotnet add package PicoQueue.Dashboard.Api
```

```csharp
app.MapPicoQueueDashboard("/jobs");
```

Visit `/jobs` to see queued, running, succeeded, and failed jobs, with retry and delete actions.

## Scaling up: swapping storage backends

Start with SQLite. When you need more (multiple instances, higher throughput, an existing Postgres/SQL Server deployment), swap the storage registration — your job code doesn't change:

```csharp
// From:
builder.Services.AddPicoQueue();

// To:
builder.Services.AddPicoQueue(options =>
    options.UseStorage<PostgresJobStorage>(connectionString));
```

See [`Docs/adding-a-storage-backend.md`](Docs/adding-a-storage-backend.md) for the full `IJobStorage` contract if you want to build a backend that isn't provided yet.

## Documentation

- 🏛️ [Architecture Overview](Docs/architecture-overview.md) — Master guide, lifecycle sequence diagrams, and end-to-end integration walkthrough.
- 📑 [PicoQueue.Abstractions](Docs/abstractions.md) — Core contracts (`IJob`, `IJob<T>`, `IJobQueue`, `IScheduleJob`), models, and attributes.
- ⚙️ [PicoQueue.Core](Docs/core.md) — Execution engine, background workers (`JobProcessor`, `RecurringJobScheduler`, `HeartbeatMonitor`, `RetentionPurgeService`), and retry policies.
- 💾 [PicoQueue.Storage.Sqlite](Docs/storage-sqlite.md) — Reference storage backend, PRAGMAs, indexing, and atomic locking transactions.

## Project structure

| Project | Purpose | Documentation |
|---|---|---|
| `PicoQueue.Abstractions` | Core contracts (`IJobQueue`, `IScheduleJob`, `QueueJob`, `JobState`) — zero dependencies | [Docs](Docs/abstractions.md) |
| `PicoQueue.Core` | The execution engine — workers, retry policy, cron scheduler, history purge | [Docs](Docs/core.md) |
| `PicoQueue.Storage.Sqlite` | Reference storage backend, built on `Microsoft.Data.Sqlite` (no EF Core) | [Docs](Docs/storage-sqlite.md) |
| `PicoQueue.Web` | Web API & Dashboard integration | Planned |

## Comparison

| | Hangfire | Quartz.NET | TickerQ | Coravel | PicoQueue |
|---|---|---|---|---|---|
| Zero-config start | ❌ requires SQL Server/Redis | ❌ requires a DB | ⚠️ requires EF Core setup | ✅ | ✅ |
| Survives restarts | ✅ | ✅ | ✅ | ❌ in-memory only | ✅ |
| ORM-free | ✅ | ✅ | ❌ (EF Core) | ✅ | ✅ |
| Dashboard included | ✅ | ❌ | ✅ | ❌ | ✅ |
| Fully open source, no paid tier | ⚠️ Pro features are commercial | ✅ | ✅ | ✅ | ✅ |

## Roadmap

- [x] Core dispatcher with retry/backoff
- [x] SQLite storage backend
- [x] Delayed & recurring (cron) jobs
- [ ] Dashboard (API + web UI)
- [ ] Postgres storage backend (community contribution target)
- [ ] SQL Server storage backend (community contribution target)
- [ ] OpenTelemetry integration

## Contributing

Contributions are welcome, and the codebase is deliberately structured to make adding a new storage backend a self-contained task — see [`CONTRIBUTING.md`](CONTRIBUTING.md) to get started, and check issues labeled [`good first issue`](../../issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22) for a place to jump in.

## License

MIT — see [`LICENSE`](LICENSE) / [`LICENSE.md`](LICENSE.md).
