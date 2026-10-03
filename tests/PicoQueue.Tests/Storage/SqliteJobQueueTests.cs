using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Enums;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Services;

namespace PicoQueue.Tests.Storage;

public class SqliteJobQueueTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SqliteJobQueue _queue;

    public SqliteJobQueueTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"picoqueue_test_queue_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath};Cache=Default";
        var connectionFactory = new SqliteConnectionFactory(_connectionString);
        var initializer = new SqliteDbInitializer(connectionFactory);
        _queue = new SqliteJobQueue(connectionFactory, initializer);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            var wal = _dbPath + "-wal";
            if (File.Exists(wal)) File.Delete(wal);
            var shm = _dbPath + "-shm";
            if (File.Exists(shm)) File.Delete(shm);
        }
        catch
        {
            // Ignore temp file cleanup exceptions
        }
    }

    [Fact]
    public async Task EnqueueAsync_ShouldPersistJobInEnqueuedState()
    {
        var dto = new EnqueueJobDto
        {
            TypeName = "TestApp.EmailJob",
            PayloadJson = "{\"to\":\"user@example.com\"}",
            Name = "Send Welcome Email",
            Queue = "emails",
            Priority = 5,
            MaxAttempts = 4
        };

        var job = await _queue.EnqueueAsync(dto);

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal("Send Welcome Email", job.Name);
        Assert.Equal("TestApp.EmailJob", job.TypeName);
        Assert.Equal("emails", job.Queue);
        Assert.Equal(JobState.Enqueued, job.State);
        Assert.Equal(5, job.Priority);
        Assert.Equal(4, job.MaxAttempts);
        Assert.Equal(0, job.Attempt);

        var fetched = await _queue.GetByIdAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal(job.Id, fetched.Id);
        Assert.Equal(JobState.Enqueued, fetched.State);
    }

    [PicoQueue.Abstractions.Attributes.Queue("critical-queue")]
    public class TestQueueAttributeJob { }

    [Fact]
    public async Task EnqueueAsync_WhenJobHasQueueAttribute_ResolvesQueueNameAutomatically()
    {
        var dto = new EnqueueJobDto
        {
            TypeName = typeof(TestQueueAttributeJob).AssemblyQualifiedName!,
            PayloadJson = "{}"
        };

        var job = await _queue.EnqueueAsync(dto);

        Assert.Equal("critical-queue", job.Queue);
    }

    [Fact]
    public async Task EnqueueAsync_WhenJobHasQueueAttribute_ExplicitQueueOverridesAttribute()
    {
        var dto = new EnqueueJobDto
        {
            TypeName = typeof(TestQueueAttributeJob).AssemblyQualifiedName!,
            PayloadJson = "{}",
            Queue = "custom-override"
        };

        var job = await _queue.EnqueueAsync(dto);

        Assert.Equal("custom-override", job.Queue);
    }

    [Fact]
    public async Task DequeueAsync_ShouldAtomicallyLockNextEligibleJob()
    {
        // Enqueue lower priority first
        await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "Job1",
            PayloadJson = "{}",
            Priority = 1,
            Queue = "default"
        });

        // Enqueue higher priority next
        var highPriJob = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "Job2_HighPriority",
            PayloadJson = "{}",
            Priority = 10,
            Queue = "default"
        });

        var dequeued = await _queue.DequeueAsync(new DequeueJobDto
        {
            WorkerId = "worker-alpha",
            Queue = "default"
        });

        Assert.NotNull(dequeued);
        Assert.Equal(highPriJob.Id, dequeued.Id);
        Assert.Equal(JobState.Processing, dequeued.State);
        Assert.Equal("worker-alpha", dequeued.WorkerId);
        Assert.NotNull(dequeued.StartedAt);
        Assert.NotNull(dequeued.HeartbeatAt);

        // Subsequent dequeue should pick up the lower priority job
        var nextDequeued = await _queue.DequeueAsync(new DequeueJobDto
        {
            WorkerId = "worker-alpha",
            Queue = "default"
        });

        Assert.NotNull(nextDequeued);
        Assert.Equal(1, nextDequeued.Priority);

        // Third dequeue should return null (queue empty)
        var empty = await _queue.DequeueAsync(new DequeueJobDto
        {
            WorkerId = "worker-alpha",
            Queue = "default"
        });

        Assert.Null(empty);
    }

    [Fact]
    public async Task AcknowledgeSuccessAsync_ShouldTransitionToSucceeded()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "JobSuccess",
            PayloadJson = "{}"
        });

        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-1" });
        Assert.NotNull(claimed);

        var ackResult = await _queue.AcknowledgeSuccessAsync(claimed.Id);
        Assert.True(ackResult);

        var updated = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(updated);
        Assert.Equal(JobState.Succeeded, updated.State);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task AcknowledgeFailureAsync_WithRetry_ShouldTransitionToRetrying()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "JobRetry",
            PayloadJson = "{}"
        });

        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-1" });
        Assert.NotNull(claimed);

        var retryTime = DateTimeOffset.UtcNow.AddMinutes(5);
        var ackResult = await _queue.AcknowledgeFailureAsync(
            claimed.Id,
            "Timeout connecting to remote host",
            "Stack trace details...",
            retryAt: retryTime);

        Assert.True(ackResult);

        var updated = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(updated);
        Assert.Equal(JobState.Retrying, updated.State);
        Assert.Equal(1, updated.Attempt);
        Assert.Equal("Timeout connecting to remote host", updated.ErrorMessage);
        Assert.Equal("Stack trace details...", updated.ErrorDetail);
        Assert.NotNull(updated.RetryAt);
    }

    [Fact]
    public async Task AcknowledgeFailureAsync_WithoutRetry_ShouldTransitionToFailed()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "JobFail",
            PayloadJson = "{}"
        });

        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-1" });
        Assert.NotNull(claimed);

        var ackResult = await _queue.AcknowledgeFailureAsync(
            claimed.Id,
            "Fatal error",
            errorDetail: null,
            retryAt: null);

        Assert.True(ackResult);

        var updated = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(updated);
        Assert.Equal(JobState.Failed, updated.State);
        Assert.Equal(1, updated.Attempt);
        Assert.NotNull(updated.CompletedAt);
    }

    [Fact]
    public async Task UpdateHeartbeatAsync_ShouldUpdateHeartbeatTimestamp()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "JobHb", PayloadJson = "{}" });
        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-1" });
        Assert.NotNull(claimed);

        var initialHeartbeat = claimed.HeartbeatAt;
        await Task.Delay(20);

        var updated = await _queue.UpdateHeartbeatAsync(claimed.Id);
        Assert.True(updated);

        var refreshed = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(refreshed);
        Assert.True(refreshed.HeartbeatAt > initialHeartbeat);
    }

    [Fact]
    public async Task GetJobsAsync_ShouldFilterAndPaginate()
    {
        for (int i = 0; i < 15; i++)
        {
            await _queue.EnqueueAsync(new EnqueueJobDto
            {
                TypeName = "Job" + i,
                PayloadJson = "{}",
                Queue = i % 2 == 0 ? "even" : "odd"
            });
        }

        var filterEven = new GetJobsFilterDto
        {
            Queue = "even",
            PageNumber = 1,
            PageSize = 5
        };

        var result = await _queue.GetJobsAsync(filterEven);
        Assert.Equal(8, result.TotalCount);
        Assert.Equal(5, result.Items.Count);
        Assert.All(result.Items, j => Assert.Equal("even", j.Queue));
    }

    [Fact]
    public async Task RecoverOrphanedJobsAsync_ShouldRequeueStaleProcessingJobs()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "JobCrash", PayloadJson = "{}" });
        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "crashed-worker" });
        Assert.NotNull(claimed);

        // Timeout of 0 means immediate staleness
        var recoveredCount = await _queue.RecoverOrphanedJobsAsync(TimeSpan.Zero);
        Assert.Equal(1, recoveredCount);

        var refreshed = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(JobState.Retrying, refreshed.State);
        Assert.Equal(1, refreshed.Attempt);
        Assert.Null(refreshed.WorkerId);
    }

    [Fact]
    public async Task RecoverOrphanedJobsAsync_WhenMaxAttemptsExceeded_TransitionsToFailed()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "PoisonJobCrash",
            PayloadJson = "{}",
            MaxAttempts = 1 // Only 1 attempt allowed
        });

        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-that-crashed" });
        Assert.NotNull(claimed);

        // Immediate staleness recovery
        var recoveredCount = await _queue.RecoverOrphanedJobsAsync(TimeSpan.Zero);
        Assert.Equal(1, recoveredCount);

        var refreshed = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(JobState.Failed, refreshed.State);
        Assert.Equal(1, refreshed.Attempt);
        Assert.Null(refreshed.RetryAt);
        Assert.Null(refreshed.WorkerId);
        Assert.Contains("orphaned", refreshed.ErrorMessage);
    }

    [Fact]
    public async Task RetryJobAsync_ShouldResetJobToEnqueued()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "JobToRetry", PayloadJson = "{}" });
        var claimed = await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "worker-1" });
        await _queue.AcknowledgeFailureAsync(claimed!.Id, "error", null, null);

        var retried = await _queue.RetryJobAsync(new RetryJobDto
        {
            JobId = claimed.Id,
            ResetAttemptCount = true
        });

        Assert.True(retried);
        var refreshed = await _queue.GetByIdAsync(claimed.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(JobState.Enqueued, refreshed.State);
        Assert.Equal(0, refreshed.Attempt);
    }

    [Fact]
    public async Task DeleteJobAsync_ShouldMarkJobAsDeleted()
    {
        var job = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "JobToDelete", PayloadJson = "{}" });

        var deleted = await _queue.DeleteJobAsync(job.Id);
        Assert.True(deleted);

        var refreshed = await _queue.GetByIdAsync(job.Id);
        Assert.NotNull(refreshed);
        Assert.Equal(JobState.Deleted, refreshed.State);
    }

    [Fact]
    public async Task EnqueueAsync_WithScheduledJobId_PersistsAndLoadsScheduledJobIdSuccessfully()
    {
        var scheduledId = Guid.NewGuid();
        var job = await _queue.EnqueueAsync(new EnqueueJobDto
        {
            TypeName = "TestApp.ScheduledChildJob",
            PayloadJson = "{}",
            ScheduledJobId = scheduledId
        });

        Assert.Equal(scheduledId, job.ScheduledJobId);

        var retrieved = await _queue.GetByIdAsync(job.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(scheduledId, retrieved.ScheduledJobId);
    }

    [Fact]
    public async Task PurgeFinishedJobsAsync_ShouldRemoveOldFinishedJobs_AndRetainActiveOrRecentJobs()
    {
        // 1. Old Succeeded job
        var j1 = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "Job1", PayloadJson = "{}" });
        await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "w1" });
        await _queue.AcknowledgeSuccessAsync(j1.Id);

        // 2. Old Failed job
        var j2 = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "Job2", PayloadJson = "{}" });
        await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "w1" });
        await _queue.AcknowledgeFailureAsync(j2.Id, "Permanent error");

        // 3. Old Deleted job
        var j3 = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "Job3", PayloadJson = "{}" });
        await _queue.DeleteJobAsync(j3.Id);

        // 4. Recent Succeeded job - MUST NOT BE PURGED
        var j5 = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "Job5", PayloadJson = "{}" });
        await _queue.DequeueAsync(new DequeueJobDto { WorkerId = "w1" });
        await _queue.AcknowledgeSuccessAsync(j5.Id);

        // 5. Old Active (Enqueued) job - MUST NOT BE PURGED
        var j4 = await _queue.EnqueueAsync(new EnqueueJobDto { TypeName = "Job4", PayloadJson = "{}" });

        // Backdate j1, j2, j3, j4 by 10 days in the database
        var pastDate = DateTimeOffset.UtcNow.AddDays(-10).ToString("O");
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE QueueJobs 
            SET LastUpdate = @PastDate 
            WHERE Id IN (@Id1, @Id2, @Id3, @Id4);";
        cmd.Parameters.AddWithValue("@PastDate", pastDate);
        cmd.Parameters.AddWithValue("@Id1", j1.Id.ToString());
        cmd.Parameters.AddWithValue("@Id2", j2.Id.ToString());
        cmd.Parameters.AddWithValue("@Id3", j3.Id.ToString());
        cmd.Parameters.AddWithValue("@Id4", j4.Id.ToString());
        await cmd.ExecuteNonQueryAsync();

        // Act: purge older than 7 days
        int purged = await _queue.PurgeFinishedJobsAsync(TimeSpan.FromDays(7));

        // Assert: 3 jobs purged (j1, j2, j3)
        Assert.Equal(3, purged);

        Assert.Null(await _queue.GetByIdAsync(j1.Id));
        Assert.Null(await _queue.GetByIdAsync(j2.Id));
        Assert.Null(await _queue.GetByIdAsync(j3.Id));

        // j4 (Enqueued) should still exist even though its LastUpdate was 10 days ago
        var remainingJ4 = await _queue.GetByIdAsync(j4.Id);
        Assert.NotNull(remainingJ4);
        Assert.Equal(JobState.Enqueued, remainingJ4.State);

        // j5 (recent Succeeded) should still exist
        var remainingJ5 = await _queue.GetByIdAsync(j5.Id);
        Assert.NotNull(remainingJ5);
        Assert.Equal(JobState.Succeeded, remainingJ5.State);
    }

    [Fact]
    public async Task PurgeFinishedJobsAsync_WithNegativeDuration_ThrowsArgumentOutOfRangeException()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _queue.PurgeFinishedJobsAsync(TimeSpan.FromSeconds(-1)));
    }
}
