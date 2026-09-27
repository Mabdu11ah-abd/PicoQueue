namespace PicoQueue.Tests.Core;

using Moq;
using PicoQueue.Abstractions.Attributes;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.DTOs.Scheduled;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Core.Extensions;

public class JobQueueExtensionsTests
{
    public class SimpleJob : IJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public record EmailPayload(string To, string Subject);

    [Queue("mail-room")]
    public class SendEmailJob : IJob<EmailPayload>
    {
        public Task ExecuteAsync(EmailPayload args, JobExecutionContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task EnqueueAsync_WithoutArgs_ResolvesDefaultsCorrectly()
    {
        var queueMock = new Mock<IJobQueue>();
        EnqueueJobDto? capturedDto = null;

        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<EnqueueJobDto>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobDto, CancellationToken>((dto, _) => capturedDto = dto)
            .ReturnsAsync(new QueueJob());

        await queueMock.Object.EnqueueAsync<SimpleJob>(priority: 5, maxAttempts: 4);

        Assert.NotNull(capturedDto);
        Assert.Equal(nameof(SimpleJob), capturedDto.Name);
        Assert.Equal(typeof(SimpleJob).AssemblyQualifiedName, capturedDto.TypeName);
        Assert.Equal("default", capturedDto.Queue);
        Assert.Equal(5, capturedDto.Priority);
        Assert.Equal(4, capturedDto.MaxAttempts);
    }

    [Fact]
    public async Task EnqueueAsync_WithArgsAndQueueAttribute_AutoResolvesQueueName()
    {
        var queueMock = new Mock<IJobQueue>();
        EnqueueJobDto? capturedDto = null;

        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<EnqueueJobDto>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobDto, CancellationToken>((dto, _) => capturedDto = dto)
            .ReturnsAsync(new QueueJob());

        var payload = new EmailPayload("test@example.com", "Hello");
        await queueMock.Object.EnqueueAsync<SendEmailJob, EmailPayload>(payload);

        Assert.NotNull(capturedDto);
        Assert.Equal(nameof(SendEmailJob), capturedDto.Name);
        Assert.Equal("mail-room", capturedDto.Queue);
        Assert.Contains("test@example.com", capturedDto.PayloadJson);
        Assert.Contains("Hello", capturedDto.PayloadJson);
    }

    [Fact]
    public async Task EnqueueAsync_WithExplicitQueue_OverridesAttribute()
    {
        var queueMock = new Mock<IJobQueue>();
        EnqueueJobDto? capturedDto = null;

        queueMock.Setup(q => q.EnqueueAsync(It.IsAny<EnqueueJobDto>(), It.IsAny<CancellationToken>()))
            .Callback<EnqueueJobDto, CancellationToken>((dto, _) => capturedDto = dto)
            .ReturnsAsync(new QueueJob());

        var payload = new EmailPayload("test@example.com", "Urgent");
        await queueMock.Object.EnqueueAsync<SendEmailJob, EmailPayload>(payload, queue: "urgent-override");

        Assert.NotNull(capturedDto);
        Assert.Equal("urgent-override", capturedDto.Queue);
    }

    [Fact]
    public async Task ScheduleAsync_WithDateTimeOffset_CreatesScheduledJobDtoCorrectly()
    {
        var schedulerMock = new Mock<IScheduleJob>();
        CreateScheduledJobDto? capturedDto = null;

        schedulerMock.Setup(s => s.ScheduleAsync(It.IsAny<CreateScheduledJobDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateScheduledJobDto, CancellationToken>((dto, _) => capturedDto = dto)
            .ReturnsAsync(new ScheduledJob());

        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(10);
        var payload = new EmailPayload("test@example.com", "Delayed");
        await schedulerMock.Object.ScheduleAsync<SendEmailJob, EmailPayload>(payload, scheduledTime);

        Assert.NotNull(capturedDto);
        Assert.Equal(nameof(SendEmailJob), capturedDto.Name);
        Assert.Equal("mail-room", capturedDto.Queue);
        Assert.False(capturedDto.IsRecurring);
        Assert.Equal(scheduledTime, capturedDto.ScheduledAt);
    }

    [Fact]
    public async Task RecurringAsync_WithCron_CreatesRecurringScheduledJobDtoCorrectly()
    {
        var schedulerMock = new Mock<IScheduleJob>();
        CreateScheduledJobDto? capturedDto = null;

        schedulerMock.Setup(s => s.ScheduleAsync(It.IsAny<CreateScheduledJobDto>(), It.IsAny<CancellationToken>()))
            .Callback<CreateScheduledJobDto, CancellationToken>((dto, _) => capturedDto = dto)
            .ReturnsAsync(new ScheduledJob());

        await schedulerMock.Object.RecurringAsync<SimpleJob>("*/5 * * * *");

        Assert.NotNull(capturedDto);
        Assert.True(capturedDto.IsRecurring);
        Assert.Equal("*/5 * * * *", capturedDto.CronExpression);
        Assert.Equal("default", capturedDto.Queue);
    }
}
