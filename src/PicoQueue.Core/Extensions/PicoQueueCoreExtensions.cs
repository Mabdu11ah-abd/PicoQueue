namespace PicoQueue.Core.Extensions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PicoQueue.Core.Execution;
using PicoQueue.Core.Monitoring;
using PicoQueue.Core.Options;
using PicoQueue.Core.Retry;
using PicoQueue.Core.Scheduling;

/// <summary>
/// Extension methods for registering PicoQueue Core services and background hosted workers.
/// </summary>
public static class PicoQueueCoreExtensions
{
    /// <summary>
    /// Registers PicoQueue Core engine services, including the job processor,
    /// recurring job scheduler, heartbeat monitor, and default execution policies.
    /// </summary>
    public static IServiceCollection AddPicoQueueCore(
        this IServiceCollection services,
        Action<PicoQueueOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Configure options
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<PicoQueueOptions>(_ => { });
        }

        // Execution and factory
        services.TryAddSingleton<IJobFactory, DefaultJobFactory>();
        services.TryAddSingleton<IRetryPolicy, DefaultRetryPolicy>();

        // Hosted Services
        services.AddHostedService<JobProcessor>();
        services.AddHostedService<RecurringJobScheduler>();
        services.AddHostedService<HeartbeatMonitor>();

        return services;
    }
}
