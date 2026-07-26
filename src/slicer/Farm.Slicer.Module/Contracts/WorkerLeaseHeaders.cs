namespace Farm.Slicer.Module.Contracts;

/// <summary>
/// Header names that bind a worker mutation to its claimed job, active lease and fencing counter.
/// </summary>
/// <remarks>
/// Every mutating worker route requires all four headers. A request that omits any of them, or that
/// presents a value which no longer matches the persisted claim, is rejected — the path fails closed.
/// </remarks>
public static class WorkerLeaseHeaders
{
    /// <summary>Registry-issued worker credential.</summary>
    public const string WorkerKey = "X-Worker-Key";

    /// <summary>Registry-issued worker service identity bound to <see cref="WorkerKey"/>.</summary>
    public const string WorkerId = "X-Worker-Id";

    /// <summary>Opaque lease token issued by a successful atomic claim.</summary>
    public const string LeaseToken = "X-Worker-Lease";

    /// <summary>Monotonic fencing counter issued by a successful atomic claim.</summary>
    public const string LeaseFence = "X-Worker-Fence";
}
