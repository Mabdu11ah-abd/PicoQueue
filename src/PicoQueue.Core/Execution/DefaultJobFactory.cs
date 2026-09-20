namespace PicoQueue.Core.Execution;

using Microsoft.Extensions.DependencyInjection;

using PicoQueue.Abstractions.Interfaces;

/// <summary>
/// Default implementation of <see cref="IJobFactory"/> that resolves jobs from the DI container.
/// </summary>
public class DefaultJobFactory : IJobFactory
{
    /// <inheritdoc/>
    public object CreateJob(IServiceProvider serviceProvider, Type jobType)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(jobType);

        if (!IsJobType(jobType))
        {
            throw new ArgumentException($"Type '{jobType.Name}' must implement {nameof(IJob)} or {typeof(IJob<>).Name}.", nameof(jobType));
        }

        // ActivatorUtilities.CreateInstance ensures that constructor parameters are resolved
        // from the provided IServiceProvider (which should be a scoped provider per job execution).
        return ActivatorUtilities.CreateInstance(serviceProvider, jobType);
    }

    /// <inheritdoc/>
    public TJob CreateJob<TJob>(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        if (!IsJobType(typeof(TJob)))
        {
            throw new ArgumentException($"Type '{typeof(TJob).Name}' must implement {nameof(IJob)} or {typeof(IJob<>).Name}.", nameof(TJob));
        }

        return ActivatorUtilities.CreateInstance<TJob>(serviceProvider);
    }

    private static bool IsJobType(Type type)
    {
        if (typeof(IJob).IsAssignableFrom(type))
        {
            return true;
        }

        return type.GetInterfaces().Any(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IJob<>));
    }
}

