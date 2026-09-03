# Contributing to PicoQueue

Thank you for your interest in contributing to PicoQueue! We welcome contributions of all kinds: bug fixes, documentation improvements, new storage backends, performance optimizations, and feature enhancements.

This document outlines the guidelines and workflows to help you get started quickly.

---

## How to Get Involved

### 1. Good First Issues
If you are new to the project, check out issues tagged with [`good first issue`](../../issues?q=is%3Aissue+is%3Aopen+label%3A%22good+first+issue%22). These issues are scoped to be self-contained and approachable.

### 2. Building a New Storage Backend
One of the primary goals of this project is a pluggable storage layer. If you'd like to implement a storage provider (such as Postgres, SQL Server, MySQL, or Redis):
- Review [`Docs/adding-a-storage-backend.md`](Docs/adding-a-storage-backend.md) for the `IJobStorage` interface specifications.
- Use `PicoQueue.Storage.Sqlite` as a reference implementation.

### 3. Reporting Bugs
When opening a bug report, please include:
- A clear, descriptive title.
- Steps to reproduce the issue.
- Expected behavior vs. actual behavior.
- .NET runtime version and OS environment.
- Relevant logs or stack traces.

### 4. Suggesting Features
Before submitting a pull request for a major feature or architectural change, please open an issue to discuss your proposal with the community.

---

## Development Setup

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or later
- Git

### Build and Test

1. **Clone the repository:**
   ```bash
   git clone https://github.com/your-org/picoqueue.git
   cd picoqueue
   ```

2. **Build the solution:**
   ```bash
   dotnet build
   ```

3. **Run tests:**
   ```bash
   dotnet test
   ```

---

## Project Structure

```text
├── src/
│   ├── PicoQueue.Abstractions/      # Core contracts (IJobStorage, JobRecord, JobState)
│   ├── PicoQueue.Core/              # Execution engine (dispatcher, retry policy, cron scheduler)
│   ├── PicoQueue.Storage.Sqlite/    # Reference zero-config SQLite storage backend
│   ├── PicoQueue.AspNetCore/        # Dependency injection and web hosting integrations
│   ├── PicoQueue.Dashboard.Api/     # Minimal API exposing job management endpoints
│   └── PicoQueue.Dashboard.Web/     # Web dashboard UI
├── tests/
│   └── PicoQueue.Tests/             # Unit and integration test suites
└── Docs/                            # Architecture and design documentation
```

---

## Coding Standards & Guidelines

- **Target Frameworks:** .NET 8.0+ standard idioms.
- **Nullable Reference Types:** Enabled across all projects (`<Nullable>enable</Nullable>`).
- **Async/Await:** All I/O operations must be fully asynchronous and accept `CancellationToken`. Avoid `.Result` or `.Wait()`.
- **Dependencies:** Keep dependencies minimal. The core abstraction and storage libraries avoid heavy ORM dependencies (e.g., direct ADO.NET / `Microsoft.Data.Sqlite` rather than Entity Framework Core).
- **Code Style:** Follow standard C# naming conventions (PascalCase for public members/types, camelCase for local variables and parameters, `_camelCase` for private fields).

---

## Pull Request Process

1. **Create a branch:**
   ```bash
   git checkout -b feature/your-feature-name
   ```
2. **Make your changes:** Write clean code with meaningful commit messages.
3. **Add tests:** Ensure new features or bug fixes have corresponding automated unit/integration tests.
4. **Run existing tests:** Make sure all tests pass (`dotnet test`).
5. **Submit your PR:** Provide a clear description of the changes and link to any relevant issues.

Thank you for helping make PicoQueue better for everyone!
