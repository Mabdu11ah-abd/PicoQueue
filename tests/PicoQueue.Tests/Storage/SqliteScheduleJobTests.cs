using PicoQueue.Abstractions.DTOs.Scheduled;
using PicoQueue.Storage.Sqlite.Connection;
using PicoQueue.Storage.Sqlite.Services;

namespace PicoQueue.Tests.Storage;

public class SqliteScheduleJobTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteScheduleJob _schedule;

    public SqliteScheduleJobTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"picoqueue_test_sched_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath};Cache=Default";
        var connectionFactory = new SqliteConnectionFactory(connectionString);
        var initializer = new SqliteDbInitializer(connectionFactory);
        _schedule = new SqliteScheduleJob(connectionFactory, initializer);
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
    public async Task ScheduleAsync_ShouldPersistDelayedJob()
    {
        var runTime = DateTimeOffset.UtcNow.AddHours(2);
        var dto = new CreateScheduledJobDto
        {
            TypeName = "DelayedReminderJob",
            PayloadJson = "{\"userId\":42}",
            ScheduledAt = runTime,
            IsRecurring = false,
            Name = "Reminder for user 42",
            Queue = "reminders"
        };

        var job = await _schedule.ScheduleAsync(dto);

        Assert.NotEqual(Guid.Empty, job.Id);
        Assert.Equal("Reminder for user 42", job.Name);
        Assert.Equal("DelayedReminderJob", job.TypeName);
        Assert.False(job.IsRecurring);
        Assert.Null(job.CronExpression);
        Assert.True(job.IsEnabled);
        Assert.Equal(runTime, job.NextRunAt);

        var fetched = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal(job.Id, fetched.Id);
    }

    [Fact]
    public async Task ScheduleAsync_ShouldPersistRecurringCronJob()
    {
        var now = DateTimeOffset.UtcNow;
        var dto = new CreateScheduledJobDto
        {
            TypeName = "NightlyCleanupJob",
            PayloadJson = "{}",
            ScheduledAt = now,
            IsRecurring = true,
            CronExpression = "0 0 * * *",
            Name = "Nightly Cleanup",
            Queue = "maintenance"
        };

        var job = await _schedule.ScheduleAsync(dto);

        Assert.True(job.IsRecurring);
        Assert.Equal("0 0 * * *", job.CronExpression);
        Assert.Equal("maintenance", job.Queue);
    }

    [Fact]
    public async Task GetDueJobsAsync_ShouldReturnOnlyDueAndEnabledJobs()
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Due job in the past
        var dueJob = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "DueJob",
            PayloadJson = "{}",
            ScheduledAt = now.AddMinutes(-5),
            IsEnabled = true
        });

        // 2. Future job
        await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "FutureJob",
            PayloadJson = "{}",
            ScheduledAt = now.AddHours(1),
            IsEnabled = true
        });

        // 3. Due job but disabled
        await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "DisabledDueJob",
            PayloadJson = "{}",
            ScheduledAt = now.AddMinutes(-10),
            IsEnabled = false
        });

        var dueJobs = await _schedule.GetDueJobsAsync(now);

        Assert.Single(dueJobs);
        Assert.Equal(dueJob.Id, dueJobs[0].Id);
    }

    [Fact]
    public async Task RecordTriggerAsync_ShouldUpdateTimestampsAndNextRun()
    {
        var now = DateTimeOffset.UtcNow;
        var job = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "JobTriggerTest",
            PayloadJson = "{}",
            ScheduledAt = now,
            IsRecurring = true,
            CronExpression = "*/5 * * * *"
        });

        var triggerTime = now;
        var nextRun = now.AddMinutes(5);

        var recorded = await _schedule.RecordTriggerAsync(job.Id, triggerTime, nextRun);
        Assert.True(recorded);

        var fetched = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal(triggerTime, fetched.LastTriggeredAt);
        Assert.Equal(nextRun, fetched.NextRunAt);
        Assert.True(fetched.IsEnabled);
    }

    [Fact]
    public async Task RecordTriggerAsync_WithoutNextRun_ShouldDisableJob()
    {
        var now = DateTimeOffset.UtcNow;
        var job = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "OneOffJob",
            PayloadJson = "{}",
            ScheduledAt = now,
            IsRecurring = false
        });

        var recorded = await _schedule.RecordTriggerAsync(job.Id, now, nextRunAt: null);
        Assert.True(recorded);

        var fetched = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Null(fetched.NextRunAt);
        Assert.False(fetched.IsEnabled);
    }

    [Fact]
    public async Task SetEnabledAsync_ShouldToggleState()
    {
        var job = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "ToggleJob",
            PayloadJson = "{}",
            ScheduledAt = DateTimeOffset.UtcNow,
            IsEnabled = true
        });

        var disabled = await _schedule.SetEnabledAsync(job.Id, false);
        Assert.True(disabled);

        var fetchedDisabled = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetchedDisabled);
        Assert.False(fetchedDisabled.IsEnabled);

        var enabled = await _schedule.SetEnabledAsync(job.Id, true);
        Assert.True(enabled);

        var fetchedEnabled = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetchedEnabled);
        Assert.True(fetchedEnabled.IsEnabled);
    }

    [Fact]
    public async Task UpdateAsync_ShouldUpdateFields()
    {
        var job = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "OriginalJob",
            PayloadJson = "{}",
            ScheduledAt = DateTimeOffset.UtcNow,
            Name = "Original Name"
        });

        var updated = await _schedule.UpdateAsync(new UpdateScheduledJobDto
        {
            JobId = job.Id,
            Name = "Renamed Job",
            Queue = "high-priority",
            IsEnabled = false
        });

        Assert.True(updated);

        var fetched = await _schedule.GetByIdAsync(job.Id);
        Assert.NotNull(fetched);
        Assert.Equal("Renamed Job", fetched.Name);
        Assert.Equal("high-priority", fetched.Queue);
        Assert.False(fetched.IsEnabled);
    }

    [Fact]
    public async Task DeleteAsync_ShouldRemoveJob()
    {
        var job = await _schedule.ScheduleAsync(new CreateScheduledJobDto
        {
            TypeName = "JobToDelete",
            PayloadJson = "{}",
            ScheduledAt = DateTimeOffset.UtcNow
        });

        var deleted = await _schedule.DeleteAsync(job.Id);
        Assert.True(deleted);

        var fetched = await _schedule.GetByIdAsync(job.Id);
        Assert.Null(fetched);
    }
}
