using System.Data.Common;
using Microsoft.Data.Sqlite;
using PicoQueue.Abstractions.Attributes;
using PicoQueue.Abstractions.Common;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Enums;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Mapping;

namespace PicoQueue.Storage.Sqlite.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IJobQueue"/> using raw ADO.NET and atomic transaction locking.
/// </summary>
public class SqliteJobQueue : IJobQueue
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SqliteDbInitializer _initializer;

    public SqliteJobQueue(ISqliteConnectionFactory connectionFactory, SqliteDbInitializer initializer)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    }

    public SqliteJobQueue(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        _connectionFactory = new SqliteConnectionFactory(connectionString);
        _initializer = new SqliteDbInitializer(_connectionFactory);
    }

    public async Task<QueueJob> EnqueueAsync(EnqueueJobDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;

        string queueName = dto.Queue;
        if (string.IsNullOrWhiteSpace(queueName) || queueName == "default")
        {
            var jobType = ResolveJobType(dto.TypeName);
            if (jobType != null)
            {
                queueName = QueueAttribute.GetQueueName(jobType, fallback: queueName ?? "default");
            }
            else
            {
                queueName = string.IsNullOrWhiteSpace(queueName) ? "default" : queueName;
            }
        }

        var job = new QueueJob
        {
            Id = Guid.NewGuid(),
            ScheduledJobId = dto.ScheduledJobId,
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.TypeName : dto.Name,
            Description = dto.Description,
            TypeName = dto.TypeName,
            PayloadJson = dto.PayloadJson,
            Queue = queueName,
            State = JobState.Enqueued,
            Priority = dto.Priority,
            Attempt = 0,
            MaxAttempts = dto.MaxAttempts > 0 ? dto.MaxAttempts : 3,
            CreatedAt = now,
            LastUpdate = now
        };

        const string sql = @"
            INSERT INTO QueueJobs (
                Id, ScheduledJobId, Name, Description, TypeName, PayloadJson, Queue, State, 
                Priority, Attempt, MaxAttempts, CreatedAt, LastUpdate
            ) VALUES (
                @Id, @ScheduledJobId, @Name, @Description, @TypeName, @PayloadJson, @Queue, @State, 
                @Priority, @Attempt, @MaxAttempts, @CreatedAt, @LastUpdate
            );";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", job.Id.ToString());
        cmd.Parameters.AddWithValue("@ScheduledJobId", (object?)job.ScheduledJobId?.ToString() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@Name", job.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)job.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TypeName", job.TypeName);
        cmd.Parameters.AddWithValue("@PayloadJson", job.PayloadJson);
        cmd.Parameters.AddWithValue("@Queue", job.Queue);
        cmd.Parameters.AddWithValue("@State", job.State.ToString());
        cmd.Parameters.AddWithValue("@Priority", job.Priority);
        cmd.Parameters.AddWithValue("@Attempt", job.Attempt);
        cmd.Parameters.AddWithValue("@MaxAttempts", job.MaxAttempts);
        cmd.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastUpdate", job.LastUpdate.ToString("O"));

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return job;
    }

    public async Task<QueueJob?> DequeueAsync(DequeueJobDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        // Acquire an immediate exclusive lock to ensure no two workers can claim the same job concurrently
        await using (var beginCmd = connection.CreateCommand())
        {
            beginCmd.CommandText = "BEGIN IMMEDIATE;";
            await beginCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var nowIso = now.ToString("O");
            var hasQueueFilter = !string.IsNullOrWhiteSpace(dto.Queue);

            // 1. Select next eligible job
            var selectSql = hasQueueFilter
                ? @"
                    SELECT Id FROM QueueJobs
                    WHERE Queue = @Queue
                      AND (
                          State = 'Enqueued'
                          OR (State = 'Retrying' AND (RetryAt IS NULL OR RetryAt <= @Now))
                      )
                    ORDER BY Priority DESC, CreatedAt ASC
                    LIMIT 1;"
                : @"
                    SELECT Id FROM QueueJobs
                    WHERE (
                          State = 'Enqueued'
                          OR (State = 'Retrying' AND (RetryAt IS NULL OR RetryAt <= @Now))
                      )
                    ORDER BY Priority DESC, CreatedAt ASC
                    LIMIT 1;";

            string? jobIdStr = null;
            await using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = selectSql;
                if (hasQueueFilter)
                {
                    selectCmd.Parameters.AddWithValue("@Queue", dto.Queue);
                }
                selectCmd.Parameters.AddWithValue("@Now", nowIso);

                var result = await selectCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (result != null && result != DBNull.Value)
                {
                    jobIdStr = result.ToString();
                }
            }

            if (jobIdStr == null)
            {
                // No job available; commit transaction and exit
                await using var commitEmptyCmd = connection.CreateCommand();
                commitEmptyCmd.CommandText = "COMMIT;";
                await commitEmptyCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }

            // 2. Claim the job and update state to Processing
            const string updateSql = @"
                UPDATE QueueJobs
                SET State = 'Processing',
                    WorkerId = @WorkerId,
                    StartedAt = COALESCE(StartedAt, @StartedAt),
                    HeartbeatAt = @HeartbeatAt,
                    LastUpdate = @LastUpdate
                WHERE Id = @Id;

                SELECT * FROM QueueJobs WHERE Id = @Id;";

            QueueJob? claimedJob = null;
            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = updateSql;
                updateCmd.Parameters.AddWithValue("@WorkerId", dto.WorkerId);
                updateCmd.Parameters.AddWithValue("@StartedAt", nowIso);
                updateCmd.Parameters.AddWithValue("@HeartbeatAt", nowIso);
                updateCmd.Parameters.AddWithValue("@LastUpdate", nowIso);
                updateCmd.Parameters.AddWithValue("@Id", jobIdStr);

                await using var reader = await updateCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    claimedJob = SqliteDataMapper.MapQueueJob(reader);
                }
            }

            // 3. Commit the transaction
            await using (var commitCmd = connection.CreateCommand())
            {
                commitCmd.CommandText = "COMMIT;";
                await commitCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return claimedJob;
        }
        catch
        {
            try
            {
                await using var rollbackCmd = connection.CreateCommand();
                rollbackCmd.CommandText = "ROLLBACK;";
                await rollbackCmd.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Suppress rollback errors if connection was lost
            }
            throw;
        }
    }

    public async Task<QueueJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM QueueJobs WHERE Id = @Id LIMIT 1;";
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return SqliteDataMapper.MapQueueJob(reader);
        }

        return null;
    }

    public async Task<PagedResult<QueueJob>> GetJobsAsync(GetJobsFilterDto filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);

        var whereClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        if (!string.IsNullOrWhiteSpace(filter.Queue))
        {
            whereClauses.Add("Queue = @Queue");
            parameters.Add(new SqliteParameter("@Queue", filter.Queue));
        }

        if (filter.State.HasValue)
        {
            whereClauses.Add("State = @State");
            parameters.Add(new SqliteParameter("@State", filter.State.Value.ToString()));
        }

        string whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

        // 1. Total count query
        int totalCount = 0;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM QueueJobs {whereSql};";
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }

            var countResult = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            totalCount = Convert.ToInt32(countResult);
        }

        // 2. Paginated rows query
        int pageNumber = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        int pageSize = filter.PageSize < 1 ? 50 : filter.PageSize;
        int offset = (pageNumber - 1) * pageSize;

        var items = new List<QueueJob>();
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = $@"
                SELECT * FROM QueueJobs 
                {whereSql}
                ORDER BY CreatedAt DESC
                LIMIT @PageSize OFFSET @Offset;";

            foreach (var p in parameters)
            {
                queryCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }
            queryCmd.Parameters.AddWithValue("@PageSize", pageSize);
            queryCmd.Parameters.AddWithValue("@Offset", offset);

            await using var reader = await queryCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(SqliteDataMapper.MapQueueJob(reader));
            }
        }

        return new PagedResult<QueueJob>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<bool> DeleteJobAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE QueueJobs 
            SET State = 'Deleted', 
                LastUpdate = @Now 
            WHERE Id = @Id AND State != 'Deleted';";

        cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> RetryJobAsync(RetryJobDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE QueueJobs
            SET State = 'Enqueued',
                Attempt = CASE WHEN @ResetAttempt = 1 THEN 0 ELSE Attempt END,
                RetryAt = NULL,
                ErrorMessage = NULL,
                ErrorDetail = NULL,
                WorkerId = NULL,
                CompletedAt = NULL,
                LastUpdate = @Now
            WHERE Id = @Id;";

        cmd.Parameters.AddWithValue("@ResetAttempt", dto.ResetAttemptCount ? 1 : 0);
        cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", dto.JobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> AcknowledgeSuccessAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        const string sql = @"
            UPDATE QueueJobs
            SET State = 'Succeeded',
                CompletedAt = @Now,
                LastUpdate = @Now
            WHERE Id = @Id AND State = 'Processing';";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> AcknowledgeFailureAsync(
        Guid jobId,
        string errorMessage,
        string? errorDetail = null,
        DateTimeOffset? retryAt = null,
        CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        bool isRetrying = retryAt.HasValue;
        string targetState = isRetrying ? JobState.Retrying.ToString() : JobState.Failed.ToString();

        const string sql = @"
            UPDATE QueueJobs
            SET State = @TargetState,
                Attempt = Attempt + 1,
                RetryAt = @RetryAt,
                ErrorMessage = @ErrorMessage,
                ErrorDetail = @ErrorDetail,
                CompletedAt = CASE WHEN @IsRetrying = 1 THEN NULL ELSE @Now END,
                LastUpdate = @Now
            WHERE Id = @Id;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@TargetState", targetState);
        cmd.Parameters.AddWithValue("@RetryAt", retryAt.HasValue ? (object)retryAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@ErrorMessage", errorMessage ?? string.Empty);
        cmd.Parameters.AddWithValue("@ErrorDetail", (object?)errorDetail ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@IsRetrying", isRetrying ? 1 : 0);
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> UpdateHeartbeatAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        const string sql = @"
            UPDATE QueueJobs
            SET HeartbeatAt = @Now,
                LastUpdate = @Now
            WHERE Id = @Id AND State = 'Processing';";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// Recovers jobs left stranded in 'Processing' state due to worker crashes or host restarts.
    /// </summary>
    public async Task<int> RecoverOrphanedJobsAsync(TimeSpan heartbeatTimeout, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var threshold = now.Subtract(heartbeatTimeout).ToString("O");
        var nowIso = now.ToString("O");

        const string sql = @"
            UPDATE QueueJobs
            SET State = CASE WHEN Attempt + 1 >= MaxAttempts THEN 'Failed' ELSE 'Retrying' END,
                Attempt = Attempt + 1,
                WorkerId = NULL,
                ErrorMessage = 'Recovered from orphaned worker heartbeat timeout',
                RetryAt = CASE WHEN Attempt + 1 >= MaxAttempts THEN NULL ELSE @Now END,
                LastUpdate = @Now
            WHERE State = 'Processing'
              AND (HeartbeatAt IS NULL OR HeartbeatAt < @Threshold);";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Threshold", threshold);
        cmd.Parameters.AddWithValue("@Now", nowIso);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Purges finished jobs (Succeeded, Failed, Deleted, Expired) whose last update timestamp is older than the specified retention duration.
    /// Returns the number of purged jobs.
    /// </summary>
    public async Task<int> PurgeFinishedJobsAsync(TimeSpan olderThan, CancellationToken ct = default)
    {
        if (olderThan < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(olderThan), "Retention duration cannot be negative.");
        }

        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var threshold = now.Subtract(olderThan).ToString("O");

        const string sql = @"
            DELETE FROM QueueJobs
            WHERE State IN ('Succeeded', 'Failed', 'Deleted', 'Expired')
              AND LastUpdate <= @Threshold;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Threshold", threshold);

        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static Type? ResolveJobType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var type = Type.GetType(typeName);
        if (type != null)
        {
            return type;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            type = assembly.GetType(typeName);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }
}
