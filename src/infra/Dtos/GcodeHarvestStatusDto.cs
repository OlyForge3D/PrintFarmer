namespace Farm.Infrastructure;

// G-code Harvesting DTOs
public enum GcodeHarvestStatusDto
{
    Running = 0,
    Completed = 1,
    Failed = 2,
    Cancelled = 3
}
