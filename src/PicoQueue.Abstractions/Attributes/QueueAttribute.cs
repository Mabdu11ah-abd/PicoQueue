using System.Reflection;

namespace PicoQueue.Abstractions.Attributes;

[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public class QueueAttribute : Attribute
{
    /// <summary>
    /// Get Job Queue Name  
    /// </summary>
    public string QueueName { get; set; }

    /// <summary>
    /// Initialize Queue Attribute
    /// </summary>
    /// <param name="queueName">Name of the Queue</param>
    public QueueAttribute(string queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            throw new ArgumentException("Queue name cannot be empty or whitespace.", nameof(queueName));
        }
        QueueName = queueName;
    }

    /// <summary>
    /// Resolves the queue name for a given job type from its [Queue] attribute, falling back to a default.
    /// </summary>
    public static string GetQueueName(Type jobType, string fallback = "default")
    {
        ArgumentNullException.ThrowIfNull(jobType);
        var attr = jobType.GetCustomAttribute<QueueAttribute>(inherit: true);
        return !string.IsNullOrWhiteSpace(attr?.QueueName) ? attr.QueueName : fallback;
    }

    /// <summary>
    /// Resolves the queue name for a given job type from its [Queue] attribute, falling back to a default.
    /// </summary>
    public static string GetQueueName<TJob>(string fallback = "default")
    {
        return GetQueueName(typeof(TJob), fallback);
    }
}

