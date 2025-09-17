using System.Collections.Concurrent;
using Farm.Web.Shared;

namespace Farm.Web.Api.Controllers.Slicing;

/// <summary>
/// Thread-safe in-memory store for simulated slicing jobs (test and dev mode).
/// </summary>
public static class SlicingJobStore
{
    private static readonly ConcurrentDictionary<string, SlicingJobDto> _jobs = new();

    public static IReadOnlyDictionary<string, SlicingJobDto> Jobs => _jobs;

    public static SlicingJobDto Add(SlicingJobDto job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _jobs[job.JobId] = job;
        return job;
    }

    public static bool TryGet(string id, out SlicingJobDto? job)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (_jobs.TryGetValue(id, out SlicingJobDto? direct))
        {
            job = direct;
            return true;
        }
        string compact = id.Replace("-", string.Empty, StringComparison.Ordinal);
        if (compact != id && _jobs.TryGetValue(compact, out SlicingJobDto? alt))
        {
            job = alt;
            return true;
        }
        job = null;
        return false;
    }
}
