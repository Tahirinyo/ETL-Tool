namespace EtlTool.Domain.Enums;

public enum EtlRunStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    PartiallyCompleted = 3,
    Failed = 4,
    Interrupted = 5
}
