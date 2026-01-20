using System.Collections.Concurrent;

namespace Farm.Web.Api.Services;

public class InMemoryGcodeUploadQuotaService(long dailyLimitBytes = 2L * 1024 * 1024 * 1024) : IGcodeUploadQuotaService
{
    private readonly long _dailyLimitBytes = dailyLimitBytes;
    private readonly ConcurrentDictionary<string, (DateOnly Day, long Bytes)> _usage = new();

    public bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes)
    {
        limitBytes = _dailyLimitBytes;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        string key = string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId;
        while (true)
        {
            (DateOnly Day, long Bytes) current = _usage.GetOrAdd(key, _ => (today, 0));
            if (current.Day != today)
            {
                if (_usage.TryUpdate(key, (today, bytes), current))
                {
                    usedBytes = bytes;
                    return usedBytes <= _dailyLimitBytes;
                }

                continue;
            }

            long newTotal = current.Bytes + bytes;
            if (_usage.TryUpdate(key, (today, newTotal), current))
            {
                usedBytes = newTotal;
                return newTotal <= _dailyLimitBytes;
            }
        }
    }
}
