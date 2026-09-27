namespace PicoQueue.Tests.Core;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PicoQueue.Abstractions.DTOs.Execution;
using PicoQueue.Abstractions.DTOs.Queue;
using PicoQueue.Abstractions.Enums;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Abstractions.Models;
using PicoQueue.Core.Execution;
using PicoQueue.Core.Options;

public class JobProcessorTests
{
    public static bool SimpleJobExecuted = false;
    public static string? ParameterizedJobResult = null;

    public class MockSimpleJob : IJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            SimpleJobExecuted = true;
            return Task.CompletedTask;
        }
    }

    public record GreetingPayload(string Name);

    public class MockGreetingJob : IJob<GreetingPayload>
    {
        public Task ExecuteAsync(GreetingPayload args, JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            ParameterizedJobResult = $"Hello, {args.Name}!";
            return Task.CompletedTask;
        }
    }

    public class MockFailingJob : IJob
    {
        public Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulation failure");
        }
    }

    [Fact]
    public async Task ProcessJob_WhenSimpleJob_ExecutesAndAcknowledgesSuccess()
    {
        SimpleJobExecuted = false;
        var serviceCollection = new ServiceCollection();
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);

        var jobQueueMock = new Mock<IJobQueue>();
        var jobFactory = new DefaultJobFactory();
        var options = Microsoft.Extensions.Options.Options.Create(new PicoQueueOptions());
        var loggerMock = new Mock<ILogger<JobProcessor>>();

        var processor = new JobProcessor(
            jobQueueMock.Object,
            jobFactory,
            scopeFactoryMock.Object,
            options,
            loggerMock.Object);

        var queueJob = new QueueJob
        {
            Id = Guid.NewGuid(),
            TypeName = typeof(MockSimpleJob).AssemblyQualifiedName!,
            PayloadJson = "{}",
            Queue = "default",
            Attempt = 0,
            MaxAttempts = 3,
            State = JobState.Processing
        };

        // We can trigger execution via the polling loop by returning the job once
        jobQueueMock.SetupSequence(q => q.DequeueAsync(It.IsAny<DequeueJobDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueJob)
            .ReturnsAsync((QueueJob?)null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await processor.StartAsync(cts.Token);
            await Task.Delay(200); // Allow background processing
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        Assert.True(SimpleJobExecuted);
        jobQueueMock.Verify(q => q.AcknowledgeSuccessAsync(queueJob.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_WhenParameterizedJob_DeserializesPayloadAndExecutes()
    {
        ParameterizedJobResult = null;
        var serviceCollection = new ServiceCollection();
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);

        var jobQueueMock = new Mock<IJobQueue>();
        var jobFactory = new DefaultJobFactory();
        var options = Microsoft.Extensions.Options.Options.Create(new PicoQueueOptions());
        var loggerMock = new Mock<ILogger<JobProcessor>>();

        var processor = new JobProcessor(
            jobQueueMock.Object,
            jobFactory,
            scopeFactoryMock.Object,
            options,
            loggerMock.Object);

        var payloadJson = JsonSerializer.Serialize(new GreetingPayload("PicoWorker"));
        var queueJob = new QueueJob
        {
            Id = Guid.NewGuid(),
            TypeName = typeof(MockGreetingJob).AssemblyQualifiedName!,
            PayloadJson = payloadJson,
            Queue = "default",
            Attempt = 0,
            MaxAttempts = 3,
            State = JobState.Processing
        };

        jobQueueMock.SetupSequence(q => q.DequeueAsync(It.IsAny<DequeueJobDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueJob)
            .ReturnsAsync((QueueJob?)null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await processor.StartAsync(cts.Token);
            await Task.Delay(200);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        Assert.Equal("Hello, PicoWorker!", ParameterizedJobResult);
        jobQueueMock.Verify(q => q.AcknowledgeSuccessAsync(queueJob.Id, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_WhenJobFails_AcknowledgesFailureWithRetry()
    {
        var serviceCollection = new ServiceCollection();
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);

        var jobQueueMock = new Mock<IJobQueue>();
        var jobFactory = new DefaultJobFactory();
        var options = Microsoft.Extensions.Options.Options.Create(new PicoQueueOptions());
        var loggerMock = new Mock<ILogger<JobProcessor>>();

        var processor = new JobProcessor(
            jobQueueMock.Object,
            jobFactory,
            scopeFactoryMock.Object,
            options,
            loggerMock.Object);

        var queueJob = new QueueJob
        {
            Id = Guid.NewGuid(),
            TypeName = typeof(MockFailingJob).AssemblyQualifiedName!,
            PayloadJson = "{}",
            Queue = "default",
            Attempt = 0,
            MaxAttempts = 3,
            State = JobState.Processing
        };

        jobQueueMock.SetupSequence(q => q.DequeueAsync(It.IsAny<DequeueJobDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueJob)
            .ReturnsAsync((QueueJob?)null);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await processor.StartAsync(cts.Token);
            await Task.Delay(200);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        jobQueueMock.Verify(q => q.AcknowledgeFailureAsync(
            queueJob.Id,
            It.Is<string>(msg => msg.Contains("Simulation failure")),
            It.IsAny<string>(),
            It.Is<DateTimeOffset?>(dt => dt.HasValue), // Verify retryAt was calculated
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessJob_WhenCustomQueuesConfigured_PollsCustomQueues()
    {
        var serviceCollection = new ServiceCollection();
        var serviceProvider = serviceCollection.BuildServiceProvider();

        var scopeFactoryMock = new Mock<IServiceScopeFactory>();
        var scopeMock = new Mock<IServiceScope>();
        scopeMock.Setup(s => s.ServiceProvider).Returns(serviceProvider);
        scopeFactoryMock.Setup(s => s.CreateScope()).Returns(scopeMock.Object);

        var jobQueueMock = new Mock<IJobQueue>();
        var jobFactory = new DefaultJobFactory();
        var options = Microsoft.Extensions.Options.Options.Create(new PicoQueueOptions
        {
            Queues = ["emails", "critical"]
        });
        var loggerMock = new Mock<ILogger<JobProcessor>>();

        var processor = new JobProcessor(
            jobQueueMock.Object,
            jobFactory,
            scopeFactoryMock.Object,
            options,
            loggerMock.Object);

        var queueJob = new QueueJob
        {
            Id = Guid.NewGuid(),
            TypeName = typeof(MockSimpleJob).AssemblyQualifiedName!,
            PayloadJson = "{}",
            Queue = "emails",
            Attempt = 0,
            MaxAttempts = 3,
            State = JobState.Processing
        };

        jobQueueMock.Setup(q => q.DequeueAsync(It.Is<DequeueJobDto>(d => d.Queue == "emails"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(queueJob);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await processor.StartAsync(cts.Token);
            await Task.Delay(200);
        }
        finally
        {
            await processor.StopAsync(CancellationToken.None);
        }

        jobQueueMock.Verify(q => q.DequeueAsync(It.Is<DequeueJobDto>(d => d.Queue == "emails"), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
