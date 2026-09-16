using Microsoft.Data.Sqlite;
using PicoQueue.Abstractions.Common;
using PicoQueue.Abstractions.DTOs.Scheduled;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Mapping;

namespace PicoQueue.Storage.Sqlite.Services;

/// <summary>
/// SQLite-backed implementation of <see cref="IScheduleJob"/> for scheduled and recurring jobs.
/// </summary>
public class SqliteScheduleJob : IScheduleJob
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly SqliteDbInitializer _initializer;

    public SqliteScheduleJob(ISqliteConnectionFactory connectionFactory, SqliteDbInitializer initializer)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
    }

    public SqliteScheduleJob(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("Connection string cannot be null or empty.", nameof(connectionString));
        }

        _connectionFactory = new SqliteConnectionFactory(connectionString);
        _initializer = new SqliteDbInitializer(_connectionFactory);
    }

    public async Task<ScheduledJob> ScheduleAsync(CreateScheduledJobDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(dto.Name) ? dto.TypeName : dto.Name,
            Description = dto.Description,
            TypeName = dto.TypeName,
            PayloadJson = dto.PayloadJson,
            Queue = string.IsNullOrWhiteSpace(dto.Queue) ? "default" : dto.Queue,
            ScheduledAt = dto.ScheduledAt,
            IsRecurring = dto.IsRecurring,
            CronExpression = dto.CronExpression,
            NextRunAt = dto.ScheduledAt,
            IsEnabled = dto.IsEnabled,
            CreatedAt = now,
            LastUpdate = now,
            UpdatedAt = now
        };

        const string sql = @"
            INSERT INTO ScheduledJobs (
                Id, Name, Description, TypeName, PayloadJson, Queue, 
                ScheduledAt, IsRecurring, CronExpression, LastTriggeredAt, 
                NextRunAt, IsEnabled, CreatedAt, LastUpdate, UpdatedAt
            ) VALUES (
                @Id, @Name, @Description, @TypeName, @PayloadJson, @Queue, 
                @ScheduledAt, @IsRecurring, @CronExpression, NULL, 
                @NextRunAt, @IsEnabled, @CreatedAt, @LastUpdate, @UpdatedAt
            );";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Id", job.Id.ToString());
        cmd.Parameters.AddWithValue("@Name", job.Name);
        cmd.Parameters.AddWithValue("@Description", (object?)job.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@TypeName", job.TypeName);
        cmd.Parameters.AddWithValue("@PayloadJson", job.PayloadJson);
        cmd.Parameters.AddWithValue("@Queue", job.Queue);
        cmd.Parameters.AddWithValue("@ScheduledAt", job.ScheduledAt.ToString("O"));
        cmd.Parameters.AddWithValue("@IsRecurring", job.IsRecurring ? 1 : 0);
        cmd.Parameters.AddWithValue("@CronExpression", (object?)job.CronExpression ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@NextRunAt", job.NextRunAt.HasValue ? (object)job.NextRunAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@IsEnabled", job.IsEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@CreatedAt", job.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@LastUpdate", job.LastUpdate.ToString("O"));
        cmd.Parameters.AddWithValue("@UpdatedAt", job.UpdatedAt.HasValue ? (object)job.UpdatedAt.Value.ToString("O") : DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return job;
    }

    public async Task<ScheduledJob?> GetByIdAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM ScheduledJobs WHERE Id = @Id LIMIT 1;";
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return SqliteDataMapper.MapScheduledJob(reader);
        }

        return null;
    }

    public async Task<PagedResult<ScheduledJob>> GetJobsAsync(GetScheduledJobsFilterDto filter, CancellationToken ct = default)
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

        if (filter.IsEnabled.HasValue)
        {
            whereClauses.Add("IsEnabled = @IsEnabled");
            parameters.Add(new SqliteParameter("@IsEnabled", filter.IsEnabled.Value ? 1 : 0));
        }

        if (filter.IsRecurring.HasValue)
        {
            whereClauses.Add("IsRecurring = @IsRecurring");
            parameters.Add(new SqliteParameter("@IsRecurring", filter.IsRecurring.Value ? 1 : 0));
        }

        string whereSql = whereClauses.Count > 0 ? "WHERE " + string.Join(" AND ", whereClauses) : string.Empty;

        // 1. Total count
        int totalCount = 0;
        await using (var countCmd = connection.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM ScheduledJobs {whereSql};";
            foreach (var p in parameters)
            {
                countCmd.Parameters.Add(new SqliteParameter(p.ParameterName, p.Value));
            }

            var countResult = await countCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            totalCount = Convert.ToInt32(countResult);
        }

        // 2. Paginated items
        int pageNumber = filter.PageNumber < 1 ? 1 : filter.PageNumber;
        int pageSize = filter.PageSize < 1 ? 50 : filter.PageSize;
        int offset = (pageNumber - 1) * pageSize;

        var items = new List<ScheduledJob>();
        await using (var queryCmd = connection.CreateCommand())
        {
            queryCmd.CommandText = $@"
                SELECT * FROM ScheduledJobs 
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
                items.Add(SqliteDataMapper.MapScheduledJob(reader));
            }
        }

        return new PagedResult<ScheduledJob>
        {
            Items = items,
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    public async Task<bool> UpdateAsync(UpdateScheduledJobDto dto, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dto);
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var updateClauses = new List<string>();
        var parameters = new List<SqliteParameter>();

        var now = DateTimeOffset.UtcNow;
        updateClauses.Add("LastUpdate = @Now");
        updateClauses.Add("UpdatedAt = @Now");
        parameters.Add(new SqliteParameter("@Now", now.ToString("O")));

        if (dto.Name != null)
        {
            updateClauses.Add("Name = @Name");
            parameters.Add(new SqliteParameter("@Name", dto.Name));
        }

        if (dto.Description != null)
        {
            updateClauses.Add("Description = @Description");
            parameters.Add(new SqliteParameter("@Description", dto.Description));
        }

        if (dto.Queue != null)
        {
            updateClauses.Add("Queue = @Queue");
            parameters.Add(new SqliteParameter("@Queue", dto.Queue));
        }

        if (dto.PayloadJson != null)
        {
            updateClauses.Add("PayloadJson = @PayloadJson");
            parameters.Add(new SqliteParameter("@PayloadJson", dto.PayloadJson));
        }

        if (dto.CronExpression != null)
        {
            updateClauses.Add("CronExpression = @CronExpression");
            parameters.Add(new SqliteParameter("@CronExpression", dto.CronExpression));
        }

        if (dto.ScheduledAt.HasValue)
        {
            updateClauses.Add("ScheduledAt = @ScheduledAt");
            parameters.Add(new SqliteParameter("@ScheduledAt", dto.ScheduledAt.Value.ToString("O")));
        }

        if (dto.IsEnabled.HasValue)
        {
            updateClauses.Add("IsEnabled = @IsEnabled");
            parameters.Add(new SqliteParameter("@IsEnabled", dto.IsEnabled.Value ? 1 : 0));
        }

        parameters.Add(new SqliteParameter("@Id", dto.JobId.ToString()));

        string sql = $"UPDATE ScheduledJobs SET {string.Join(", ", updateClauses)} WHERE Id = @Id;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var p in parameters)
        {
            cmd.Parameters.Add(p);
        }

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(Guid jobId, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM ScheduledJobs WHERE Id = @Id;";
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<bool> SetEnabledAsync(Guid jobId, bool isEnabled, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        const string sql = @"
            UPDATE ScheduledJobs 
            SET IsEnabled = @IsEnabled, 
                LastUpdate = @Now, 
                UpdatedAt = @Now 
            WHERE Id = @Id;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@IsEnabled", isEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetDueJobsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        const string sql = @"
            SELECT * FROM ScheduledJobs
            WHERE IsEnabled = 1
              AND NextRunAt IS NOT NULL
              AND NextRunAt <= @Now
            ORDER BY NextRunAt ASC;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));

        var results = new List<ScheduledJob>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            results.Add(SqliteDataMapper.MapScheduledJob(reader));
        }

        return results;
    }

    public async Task<bool> RecordTriggerAsync(Guid jobId, DateTimeOffset triggeredAt, DateTimeOffset? nextRunAt, CancellationToken ct = default)
    {
        await _initializer.InitializeAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        const string sql = @"
            UPDATE ScheduledJobs
            SET LastTriggeredAt = @TriggeredAt,
                NextRunAt = @NextRunAt,
                LastUpdate = @Now,
                UpdatedAt = @Now,
                IsEnabled = CASE WHEN @NextRunAt IS NULL THEN 0 ELSE IsEnabled END
            WHERE Id = @Id;";

        await using var connection = await _connectionFactory.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@TriggeredAt", triggeredAt.ToString("O"));
        cmd.Parameters.AddWithValue("@NextRunAt", nextRunAt.HasValue ? (object)nextRunAt.Value.ToString("O") : DBNull.Value);
        cmd.Parameters.AddWithValue("@Now", now.ToString("O"));
        cmd.Parameters.AddWithValue("@Id", jobId.ToString());

        int affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        return affected > 0;
    }
}
