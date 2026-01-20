using System.Collections.Concurrent;

namespace Farm.Web.Api.Services;

public class InMemoryGcodeUploadSettings : IGcodeUploadSettings
{
    private readonly ConcurrentDictionary<string, byte> _extensions = new(StringComparer.Ordinal);

    public InMemoryGcodeUploadSettings()
    {
        // Seed from environment variable or defaults
        string? env = Environment.GetEnvironmentVariable("GCODE_ALLOWED_EXTENSIONS");
        string[] list = string.IsNullOrWhiteSpace(env) ? new[] { ".gcode", ".bgcode" } : env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string e in list)
        {
            string norm = e.StartsWith('.') ? e : "." + e;
            _ = _extensions.TryAdd(norm, 0);
        }
    }

    public IReadOnlyCollection<string> GetAllowedExtensions() => _extensions.Keys.ToArray();

    public void UpdateAllowedExtensions(IEnumerable<string> extensions)
    {
        List<string> cleaned = extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim())
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        _extensions.Clear();
        foreach (string? e in cleaned)
        {
            _ = _extensions.TryAdd(e, 0);
        }
    }
}
