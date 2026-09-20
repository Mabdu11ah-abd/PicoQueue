namespace PicoQueue.Core.Execution;

/// <summary>
/// Defines a factory abstraction for instantiating background job instances.
/// </summary>
public interface IJobFactory
{
    /// <summary>
    /// Creates an instance of the specified job type using the provided service provider.
    /// </summary>
    /// <param name="serviceProvider">The scoped service provider used to resolve dependencies.</param>
    /// <param name="jobType">The runtime type of the job to instantiate.</param>
    /// <returns>An instance of the requested job.</returns>
    object CreateJob(IServiceProvider serviceProvider, Type jobType);

    /// <summary>
    /// Creates a strongly-typed instance of the specified job type using the provided service provider.
    /// </summary>
    /// <typeparam name="TJob">The runtime type of the job to instantiate.</typeparam>
    /// <param name="serviceProvider">The scoped service provider used to resolve dependencies.</param>
    /// <returns>An instance of the requested job.</returns>
    TJob CreateJob<TJob>(IServiceProvider serviceProvider) => (TJob)CreateJob(serviceProvider, typeof(TJob));
}
