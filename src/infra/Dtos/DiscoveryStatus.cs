namespace Farm.Infrastructure;

/// <summary>
/// States representing the lifecycle of a discovery session.
/// </summary>
public enum DiscoveryStatus
{
    Starting,
    Scanning,
    Completed,
    Cancelled,
    Error
}
#pragma warning restore CA1002
