using System.Collections.Concurrent;
using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Api.Controllers.Slicing;

/// <summary>
/// In-memory store for legacy slicing jobs (pre-queue system).
/// Thread-safe concurrent dictionary backing.
/// </summary>
public static class SlicingJobStore
{
    private static readonly ConcurrentDictionary<Guid, SlicingJobDto> _jobs = new();

    /// <summary>Gets all stored jobs.</summary>
#pragma warning disable CA1024 // Use properties where appropriate — method is more expressive for collection access
    public static IEnumerable<SlicingJobDto> GetAll() => _jobs.Values;
#pragma warning restore CA1024

    /// <summary>Gets a job by ID.</summary>
    public static SlicingJobDto? Get(Guid id) =>
        _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Adds or updates a job.</summary>
    public static void AddOrUpdate(Guid id, SlicingJobDto job) =>
        _jobs[id] = job;

    /// <summary>Removes a job by ID.</summary>
    public static bool Remove(Guid id) =>
        _jobs.TryRemove(id, out _);

    /// <summary>Gets the number of stored jobs.</summary>
    public static int Count => _jobs.Count;

    /// <summary>Clears all stored jobs.</summary>
    public static void Clear() => _jobs.Clear();
}
