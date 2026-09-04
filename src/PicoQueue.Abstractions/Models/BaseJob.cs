namespace PicoQueue.Abstractions.Models;


// base schema of all jobs, this table will be used to store all jobs
public abstract class BaseJob
{
    // Primary key
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string Queue { get; set; } = "default";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastUpdate { get; set; } = DateTimeOffset.UtcNow;
}
