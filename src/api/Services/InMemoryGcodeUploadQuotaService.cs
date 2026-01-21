using System.Collections.Concurrent;
using Farm.Infrastructure.Settings;

namespace Farm.Web.Api.Services;

/// <summary>
/// Tracks per-user daily upload quota usage in memory.
/// Reads the daily limit from the persisted GcodeUploadSettings.
/// </summary>
public class InMemoryGcodeUploadQuotaService : IGcodeUploadQuotaService
{
    private readonly ISettingsService _settingsService;
    private readonly ConcurrentDictionary<string, (DateOnly Day, long Bytes)> _usage = new();

    public InMemoryGcodeUploadQuotaService(ISettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    private long GetDailyLimitBytes()
    {
        GcodeUploadSettings? settings = _settingsService.Get<GcodeUploadSettings>();
        return settings?.DailyUploadLimitBytes ?? 2L * 1024 * 1024 * 1024; // Default 2GB
    }

    public bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes)
    {
        limitBytes = GetDailyLimitBytes();
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
                    return usedBytes <= limitBytes;
                }

                continue;
            }

            long newTotal = current.Bytes + bytes;
            if (_usage.TryUpdate(key, (today, newTotal), current))
            {
                usedBytes = newTotal;
                return newTotal <= limitBytes;
            }
        }
    }
}
