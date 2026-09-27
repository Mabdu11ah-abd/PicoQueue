namespace PicoQueue.Core.Options;

using System.ComponentModel.DataAnnotations;

public sealed class PicoQueueOptions
{
    public const string SectionName = "PicoQueue";

    /// <summary>
    /// Maximum number of background jobs processed in parallel.
    /// </summary>
    [Range(1, 1000, ErrorMessage = "{0} must be between {1} and {2}.")]
    public int MaxConcurrentJobs { get; set; } = Environment.ProcessorCount * 2;

    /// <summary>
    /// Interval between polling passes for new queue items.
    /// </summary>
    [Required]
    public TimeSpan QueuePollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Interval between checks for delayed/scheduled jobs.
    /// </summary>
    [Required]
    public TimeSpan SchedulePollingInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Interval between worker heartbeat checks to detect dead instances.
    /// </summary>
    [Required]
    public TimeSpan HeartBeatMonitorInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Job Queue Name
    /// </summary>
    public string QueueName { get; set; } = "default";

    /// <summary>
    /// Ordered list of queue names to poll. If specified and non-empty, the worker polls these queues in priority order.
    /// </summary>
    public string[] Queues { get; set; } = [];
}