using System.Collections.Concurrent;

namespace Farm.Web.Api.Services;

public interface IGcodeUploadSettings
{
    IReadOnlyCollection<string> AllowedExtensions { get; }
    void UpdateAllowedExtensions(IEnumerable<string> extensions);
}

public class InMemoryGcodeUploadSettings : IGcodeUploadSettings
{
    private readonly ConcurrentDictionary<string, byte> _extensions = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryGcodeUploadSettings()
    {
        // Seed from environment variable or defaults
        string? env = Environment.GetEnvironmentVariable("GCODE_ALLOWED_EXTENSIONS");
        string[] list = string.IsNullOrWhiteSpace(env) ? [".gcode", ".bgcode"] : env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string e in list)
        {
            string norm = e.StartsWith('.') ? e : "." + e;
            _extensions.TryAdd(norm, 0);
        }
    }

    public IReadOnlyCollection<string> AllowedExtensions => _extensions.Keys.ToArray();

    public void UpdateAllowedExtensions(IEnumerable<string> extensions)
    {
        List<string> cleaned = extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _extensions.Clear();
        foreach (string? e in cleaned)
        {
            _extensions.TryAdd(e, 0);
        }
    }
}

public interface IGcodeUploadQuotaService
{
    bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes);
}

public class InMemoryGcodeUploadQuotaService(long dailyLimitBytes = 2L * 1024 * 1024 * 1024) : IGcodeUploadQuotaService
{
    private readonly long _dailyLimitBytes = dailyLimitBytes;
    private readonly ConcurrentDictionary<string, (DateOnly day, long bytes)> _usage = new();

    public bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes)
    {
        limitBytes = _dailyLimitBytes;
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        string key = string.IsNullOrWhiteSpace(userId) ? "anonymous" : userId;
        while (true)
        {
            (DateOnly day, long bytes) current = _usage.GetOrAdd(key, _ => (today, 0));
            if (current.day != today)
            {
                if (_usage.TryUpdate(key, (today, bytes), current))
                {
                    usedBytes = bytes;
                    return usedBytes <= _dailyLimitBytes;
                }
                continue;
            }
            long newTotal = current.bytes + bytes;
            if (_usage.TryUpdate(key, (today, newTotal), current))
            {
                usedBytes = newTotal;
                return newTotal <= _dailyLimitBytes;
            }
        }
    }
}
