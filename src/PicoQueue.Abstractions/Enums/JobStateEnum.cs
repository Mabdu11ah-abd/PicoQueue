namespace PicoQueue.Abstractions.Enums;

public enum JobState
{
    Enqueued,
    Processing,
    Succeeded,
    Failed,
    Retrying,
    Deleted,
    Expired
}

