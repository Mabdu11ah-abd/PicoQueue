using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Monitoring;
using PicoQueue.Core.Options;

namespace PicoQueue.Tests.Core;

public class RetentionPurgeServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenIntervalIsZeroOrNegative_DoesNotCallPurge()
    {
        var jobQueueMock = new Mock<IJobQueue>();
        var options = Options.Create(new PicoQueueOptions
        {
            RetentionPurgeInterval = TimeSpan.Zero,
            RetentionPeriod = TimeSpan.FromDays(7)
        });
        var loggerMock = new Mock<ILogger<RetentionPurgeService>>();

        var service = new RetentionPurgeService(jobQueueMock.Object, options, loggerMock.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);
        await Task.Delay(50);
        await service.StopAsync(cts.Token);

        jobQueueMock.Verify(q => q.PurgeFinishedJobsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTriggered_CallsPurgeFinishedJobsAsync()
    {
        var jobQueueMock = new Mock<IJobQueue>();
        jobQueueMock
            .Setup(q => q.PurgeFinishedJobsAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var options = Options.Create(new PicoQueueOptions
        {
            RetentionPurgeInterval = TimeSpan.FromMilliseconds(50),
            RetentionPeriod = TimeSpan.FromDays(14)
        });
        var loggerMock = new Mock<ILogger<RetentionPurgeService>>();

        var service = new RetentionPurgeService(jobQueueMock.Object, options, loggerMock.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await service.StartAsync(cts.Token);

        // Wait long enough for at least one tick
        await Task.Delay(120);

        await service.StopAsync(CancellationToken.None);

        jobQueueMock.Verify(q => q.PurgeFinishedJobsAsync(TimeSpan.FromDays(14), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
