namespace PicoQueue.Tests.Core;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Core.Options;
using PicoQueue.Core.Scheduling;

public class RecurringJobSchedulerTests
{
    [Fact]
    public async Task Scheduler_WhenDueJobExists_EnqueuesJobAndRecordsTrigger()
    {
        var jobQueueMock = new Mock<IJobQueue>();
        var scheduleJobMock = new Mock<IScheduleJob>();
        var options = Microsoft.Extensions.Options.Options.Create(new PicoQueueOptions
        {
            SchedulePollingInterval = TimeSpan.FromMilliseconds(500)
        });
        var loggerMock = new Mock<ILogger<RecurringJobScheduler>>();

        var scheduledJob = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            Name = "HourlySync",
            TypeName = "MyApp.SyncJob",
            PayloadJson = "{}",
            Queue = "default",
            IsRecurring = true,
            CronExpression = "0 * * * *", // every hour
            NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            IsEnabled = true
        };

        scheduleJobMock
            .Setup(s => s.GetDueJobsAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ScheduledJob> { scheduledJob });

        var scheduler = new RecurringJobScheduler(
            jobQueueMock.Object,
            scheduleJobMock.Object,
            options,
            loggerMock.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await scheduler.StartAsync(cts.Token);
            await Task.Delay(200);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }

        // Verify it was enqueued to IJobQueue
        jobQueueMock.Verify(q => q.EnqueueAsync(
            It.Is<EnqueueJobDto>(dto => dto.TypeName == "MyApp.SyncJob" && dto.Name == "HourlySync"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);

        // Verify trigger was recorded and next occurrence was calculated
        scheduleJobMock.Verify(s => s.RecordTriggerAsync(
            scheduledJob.Id,
            It.IsAny<DateTimeOffset>(),
            It.Is<DateTimeOffset?>(next => next.HasValue && next.Value > DateTimeOffset.UtcNow),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
