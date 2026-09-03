# PicoQueue — Implementation Plan & Milestones

This plan outlines the staged development milestones for PicoQueue, moving from foundational contracts to a full production-ready ecosystem with dashboards and multiple storage backends.

---

## Milestone 1: Core Abstractions & SQLite Reference Backend

- [x] **Project Scaffolding & Solution Setup**
  - .NET 8 / 9 target frameworks
  - Directory structure (`Abstractions`, `Core`, `Storage.Sqlite`, `AspNetCore`, `Dashboard`, `Tests`)
- [x] **Core Abstractions (`PicoQueue.Abstractions`)**
  - Define `IJobStorage` interface
  - `JobRecord`, `JobState` enum, `JobPayload`, `JobQuery`, `PagedResult<T>`
  - `IJobSerializer` (System.Text.Json default implementation)
- [x] **SQLite Storage Provider (`PicoQueue.Storage.Sqlite`)**
  - `Microsoft.Data.Sqlite` implementation (no EF Core)
  - Auto-initialization of schema, WAL mode, pragmas
  - Atomic pickup with transaction lock handling
  - Heartbeat updates and orphaned job recovery
- [ ] **Core Engine (`PicoQueue.Core`)**
  - In-process signaling dispatcher with concurrency throttling
  - Retry engine with exponential backoff & jitter
  - Cron expression parser and recurring job scheduler

---

## Milestone 2: ASP.NET Core Integration & Developer Experience

- [ ] **Dependency Injection Integration (`PicoQueue.AspNetCore`)**
  - `services.AddPicoQueue(options => ...)`
  - Background worker registration via `IHostedService`
  - Graceful shutdown and in-flight job completion handling
- [ ] **Comprehensive Test Suite (`PicoQueue.Tests`)**
  - Unit tests for dispatcher, retries, and cron scheduling
  - Reusable `JobStorageTestSuite<TStorage>` contract verification
  - Concurrency stress tests for SQLite storage

---

## Milestone 3: Dashboard & Observability

- [ ] **Dashboard API (`PicoQueue.Dashboard.Api`)**
  - Minimal API endpoints for listing jobs, stats, retry/delete actions
  - `app.MapPicoQueueDashboard("/jobs")`
- [ ] **Dashboard Web UI (`PicoQueue.Dashboard.Web`)**
  - Lightweight UI showing realtime queued, running, failed, and recurring jobs
  - Direct execution and manual retry controls
- [ ] **Telemetry Integration**
  - OpenTelemetry `ActivitySource` tracing and `Meter` metrics

---

## Milestone 4: Community Storage Providers

- [ ] **PostgreSQL Backend (`PicoQueue.Storage.Postgres`)**
  - Direct ADO.NET / Npgsql implementation using `FOR UPDATE SKIP LOCKED`
- [ ] **SQL Server Backend (`PicoQueue.Storage.SqlServer`)**
  - Direct ADO.NET / Microsoft.Data.SqlClient with `ROWLOCK, READPAST, UPDLOCK`
- [ ] **MySQL Backend (`PicoQueue.Storage.MySql`)**

---

## Milestone 5: Documentation & Package Publishing

- [x] Root `Readme.md`, `LICENSE`, `CONTRIBUTING.md`
- [x] Guide for adding custom storage backends (`Docs/adding-a-storage-backend.md`)
- [ ] NuGet package metadata and automated CI/CD GitHub Actions
