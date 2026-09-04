using PicoQueue.Abstractions.DTOs.Execution;

namespace PicoQueue.Abstractions.Interfaces;

/// <summary>
/// Contract for a background job that requires no input arguments.
/// </summary>
public interface IJob
{
    /// <summary>
    /// Executes the background job logic.
    /// </summary>
    /// <param name="context">Execution metadata about the current job and attempt.</param>
    /// <param name="cancellationToken">Cancellation token triggered during application shutdown.</param>
    Task ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Contract for a background job with strongly-typed input arguments.
/// </summary>
/// <typeparam name="TArgs">The type of payload data required by this job.</typeparam>
public interface IJob<in TArgs>
{
    /// <summary>
    /// Executes the background job logic with strongly-typed input arguments.
    /// </summary>
    /// <param name="args">The deserialized payload arguments.</param>
    /// <param name="context">Execution metadata about the current job and attempt.</param>
    /// <param name="cancellationToken">Cancellation token triggered during application shutdown.</param>
    Task ExecuteAsync(TArgs args, JobExecutionContext context, CancellationToken cancellationToken = default);
}
