namespace Farm.Infrastructure.Domain;

public enum HarvestFileStatus
{
    Pending = 0,
    InProgress = 1,
    Complete = 2,
    Failed = 3,
    Cancelled = 4,
    Skipped = 5
}
