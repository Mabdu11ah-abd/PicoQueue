# PicoQueue — A Lean, SQLite-First Background Job Queue for .NET

## 1. Problem Statement

Every non-trivial backend eventually needs to run work outside the request/response cycle: sending emails, processing uploads, generating reports, syncing data, retrying failed webhooks. The .NET ecosystem's default answer to this has been **Hangfire**, and for good reason — it's flexible and has a genuinely great dashboard. But teams reaching for alternatives in 2026 keep running into the same friction points:

- **Storage overhead for small workloads.** Hangfire polls its backing store continuously to discover due jobs. For an app with a handful of nightly jobs, that means recurring SQL queries against SQL Server or Redis forever, just to find out nothing is due yet.
- **Licensing ambiguity.** Hangfire's core is open source (LGPL v3), but several features teams actually want in production (SQL Server dashboard extensions, batch jobs at scale, priority queues, some pro-tier dashboard capabilities) sit behind **Hangfire.Pro**, a paid commercial license. This pushes cost-conscious teams and OSS-first shops to look elsewhere, but the free alternatives all give something up.
- **Heavy footprint for simple use cases.** Hangfire and Quartz.NET were both built for enterprise-scale job processing. A solo developer or small team that just wants reliable, persistent, fire-and-forget jobs with a queryable history is paying (in complexity, infrastructure, and cognitive overhead) for capabilities they don't need.
- **A gap between "no persistence" and "full infrastructure."** On one end you have lightweight in-process schedulers (Coravel, plain `BackgroundService`) that lose all state on restart and assume a single instance. On the other end you have Hangfire, Quartz.NET, and heavier durable-execution platforms like Temporal that require real infrastructure and operational buy-in. There's no well-established, embeddable option that persists jobs to a **local file** (SQLite) with zero external infrastructure, while still surviving process restarts and giving developers a dashboard and retry semantics.
- **Extensibility is inconsistent across the field.** Most existing libraries hard-code their storage layer or make swapping backends a significant undertaking, which limits both what the library can be used for and how easily the community can extend it.

In short: developers want something **between** Coravel's simplicity and Hangfire's power, that is free of licensing gray areas, is trivially embeddable with no external services required to get started, and has a genuinely pluggable architecture from day one.

## 2. Proposed Solution

Build an **embeddable, open-source (MIT-licensed) background job queue for .NET**, SQLite-first but storage-agnostic by design, with a clean plugin interface for both **storage backends** and **job types**.

### Core principles

1. **Zero infrastructure to start.** `dotnet add package PicoQueue` and jobs persist to a local SQLite file with no separate server, no Redis, no SQL Server setup. Production teams can later swap in Postgres or SQL Server via the same interface.
2. **No licensing ambiguity.** Fully MIT-licensed, including whatever ships as the "dashboard" — no pro tier, no feature paywall. This is a direct, explicit differentiator against Hangfire.
3. **Efficient by default.** Avoid unnecessary polling; use event-based wake-ups where possible (e.g., in-process signaling for enqueue-to-run latency), falling back to lightweight polling only for scheduled/cron jobs.
4. **Pluggable from the core outward.** Storage backends and job execution strategies are implemented against a small, well-documented interface (`IJobStorage`, `IJobExecutor` or similar), so the "add a new backend" contribution path is realistic for an external contributor without deep familiarity with the whole codebase.
5. **Batteries-included but optional.** A minimal, self-hosted dashboard ships in a separate NuGet package so the core stays dependency-light for people who only want the queue.

### High-level architecture

- **Core library**: job definitions, enqueue/schedule API, execution pipeline, retry/backoff policy engine, job state machine (Enqueued → Processing → Succeeded / Failed / Retrying / Deleted).
- **Storage abstraction layer**: `IJobStorage` interface with a reference **SQLite** implementation shipped in core; Postgres and SQL Server as separate, community-extensible packages.
- **Worker/dispatcher**: in-process background service that picks up due jobs, respects concurrency limits, and executes with cancellation support.
- **Dashboard (separate package)**: lightweight web UI (Blazor or minimal API + simple frontend) showing queued/running/failed jobs, retry history, and manual retry/delete actions.
- **Recurring job scheduler**: cron-expression support for recurring jobs, built on the same storage abstraction.

## 3. Functional Requirements

### 3.1 Job definition & enqueueing
- FR1: Developers can enqueue a fire-and-forget job from application code with a single method call.
- FR2: Developers can schedule a job to run at a specific future time (delayed jobs).
- FR3: Developers can define recurring jobs using standard cron expressions.
- FR4: Job payloads support strongly typed parameters (serialized, not just strings).
- FR5: Jobs can be enqueued with a priority or queue name to support basic prioritization/isolation of workloads.

### 3.2 Execution
- FR6: A configurable number of worker threads/concurrency slots process jobs from the queue.
- FR7: Jobs support cooperative cancellation via `CancellationToken`.
- FR8: Failed jobs are retried automatically according to a configurable backoff policy (e.g., exponential backoff, max attempts).
- FR9: Jobs that exhaust retries move to a distinguishable "Failed" state rather than being silently dropped.
- FR10: Long-running or stuck jobs can be detected (heartbeat/timeout) and requeued or flagged.

### 3.3 Persistence & storage
- FR11: A reference SQLite storage implementation ships in the core package with zero additional configuration required.
- FR12: The storage layer is defined by a public interface so alternative backends (Postgres, SQL Server, in-memory for tests) can be implemented as separate packages.
- FR13: Job state survives application/process restarts when using a persistent backend.
- FR14: Job history (including past executions, failures, and stack traces) is retained and queryable for a configurable retention period.

### 3.4 Observability & management
- FR15: A dashboard package provides a web UI to view queued, running, succeeded, and failed jobs.
- FR16: The dashboard supports manually triggering, retrying, or deleting a job.
- FR17: Core metrics (throughput, queue depth, failure rate) are exposed for logging/metrics integration (e.g., via `ILogger`, OpenTelemetry hooks).

### 3.5 Extensibility
- FR18: New storage backends can be added by implementing a documented interface, without modifying core library code.
- FR19: The project ships at least one reference backend (SQLite) fully tested and documented as a template for contributors adding new backends.
- FR20: Job execution middleware/hooks (e.g., logging, metrics, custom retry logic) can be registered without forking the core pipeline.

### 3.6 Developer experience
- FR21: Setup requires no more than a NuGet install and a couple of lines of DI registration to get a working job queue.
- FR22: Comprehensive quick-start documentation and a contribution guide with clearly labeled "good first issue" tasks.
- FR23: Full async/await support throughout the API surface; no blocking calls in the core execution path.

## 4. Competitor Analysis

| | **Hangfire** | **Quartz.NET** | **TickerQ** | **Coravel** | **PicoQueue** |
|---|---|---|---|---|---|
| **Primary focus** | General background jobs (enqueue/retry/dashboard) | Scheduling precision (cron, calendars, misfire handling) | Modern source-generated scheduler + jobs | Simple in-process scheduling | Embeddable job queue, SQLite-first |
| **Persistence** | SQL Server, Redis, others via extensions | DB-backed (many providers), supports clustering | EF Core (Postgres/SQL Server/SQLite/MySQL) or Redis | None — in-memory only | SQLite by default; Postgres/SQL Server as pluggable backends |
| **Infra required to start** | Requires SQL Server or Redis from day one | Requires a DB for persistence/clustering | Requires EF Core + a configured DB | None | None — local file, zero setup |
| **Dashboard** | Yes, mature and widely used | No built-in dashboard | Yes — real-time, SignalR-based | No | Yes — planned, MIT-licensed, no pro tier |
| **Licensing** | Core is LGPL v3; advanced features (Pro dashboard extensions, batches at scale, priority queues) are commercial | Apache 2.0 | MIT | MIT | MIT, fully open, no paywalled tier |
| **Execution model** | Polling-based against storage | Polling/trigger-based, very mature | Source-generated, reflection-free, no polling loop | In-process only, fluent API | Event-driven where possible; minimal polling |
| **Maturity** | ~13+ years, very battle-tested | ~20+ years, extremely mature | Young (newcomer, actively evolving) | Established but scope-limited | New |
| **Extensibility of storage layer** | Possible but not the primary design focus | Provider model exists but oriented around enterprise DBs | Coupled to EF Core specifically | N/A (no persistence) | Core design goal — explicit plugin interface, SQLite as reference implementation |
| **Best fit today** | Teams wanting a mature all-rounder and willing to accept SQL Server/Redis + possible Pro licensing | Teams needing complex calendars, misfire policies, clustering | Greenfield EF Core shops wanting a modern dashboard without Hangfire's weight | Small single-instance apps, jobs that can tolerate missed runs | Small-to-mid teams wanting a real job queue with zero infra and no licensing ambiguity, that can grow into Postgres/SQL Server later |

### Key takeaways from the competitive landscape
- Quartz.NET is the most mature and capable scheduler, offering rich cron triggers, calendars, and misfire policies, but has no dashboard and more setup ceremony. Hangfire remains the most popular general-purpose background-job system, covering fire-and-forget, delayed, and recurring jobs with a strong dashboard, though recurring cron is more of an add-on than its core strength.
- TickerQ is the newer entrant — source-generator based with no reflection, EF Core persistence, a live dashboard, and no polling loop — but is still young, and Coravel is the simplest option, offering zero-infrastructure in-process scheduling at the cost of persistence (a restart loses all state, and it assumes a single instance).
- TickerQ's reliance on Entity Framework Core couples job scheduling directly to the application's database strategy, which creates friction for teams preferring Dapper, raw SQL, or NoSQL databases.
- A recurring complaint about Hangfire is that its storage load is disproportionate for light workloads — it polls its store continuously (e.g., SQL Server queries every few seconds) even to discover that nothing is due, which matters for teams running only a handful of nightly jobs.

**Net positioning:** there is a real, currently unfilled niche for a job queue that combines (a) genuinely zero-infrastructure setup via SQLite, (b) an unambiguous MIT license with no paid tier, and (c) a storage abstraction designed from the start to be extended by the community rather than bolted on later. TickerQ is the closest existing competitor in spirit (modern, dashboard-equipped, actively developed) but is coupled to EF Core and doesn't lead with a zero-config local-file story; Coravel covers the zero-infra angle but abandons persistence entirely. PicoQueue sits deliberately between them.

## 5. Next Steps
- Finalize and verify NuGet/GitHub namespace availability.
- Decide on dashboard tech stack (Blazor vs. minimal API + lightweight JS frontend) — affects dependency footprint of the dashboard package.
- Implement channel-based dispatcher and core execution pipeline.
- Implement reference `PicoQueue.Storage.Sqlite` backend.
- Scope the first milestone and publish with `CONTRIBUTING.md` and initial "good first issue" tasks.
