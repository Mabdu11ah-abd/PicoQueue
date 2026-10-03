using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using PicoQueue.Abstractions.Interfaces;
using PicoQueue.Core.Execution;
using PicoQueue.Core.Extensions;
using PicoQueue.Core.Monitoring;
using PicoQueue.Core.Options;
using PicoQueue.Core.Retry;
using PicoQueue.Core.Scheduling;

public class PicoQueueCoreExtensionsTests
{
    [Fact]
    public void AddPicoQueueCore_RegistersExpectedServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new Mock<IJobQueue>().Object);
        services.AddSingleton(new Mock<IScheduleJob>().Object);

        services.AddPicoQueueCore(options =>
        {
            options.MaxConcurrentJobs = 16;
            options.QueuePollingInterval = TimeSpan.FromSeconds(5);
        });

        var provider = services.BuildServiceProvider();

        // 1. Verify Options configured
        var options = provider.GetRequiredService<IOptions<PicoQueueOptions>>().Value;
        Assert.Equal(16, options.MaxConcurrentJobs);
        Assert.Equal(TimeSpan.FromSeconds(5), options.QueuePollingInterval);

        // 2. Verify Singletons
        var jobFactory = provider.GetService<IJobFactory>();
        Assert.NotNull(jobFactory);
        Assert.IsType<DefaultJobFactory>(jobFactory);

        var retryPolicy = provider.GetService<IRetryPolicy>();
        Assert.NotNull(retryPolicy);
        Assert.IsType<DefaultRetryPolicy>(retryPolicy);

        // 3. Verify Hosted Services are registered
        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, s => s is JobProcessor);
        Assert.Contains(hostedServices, s => s is RecurringJobScheduler);
        Assert.Contains(hostedServices, s => s is HeartbeatMonitor);
        Assert.Contains(hostedServices, s => s is RetentionPurgeService);
    }
}
