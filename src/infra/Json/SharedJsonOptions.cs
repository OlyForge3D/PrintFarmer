using System.Text.Json;
using System.Text.Json.Serialization;

namespace Farm.Infrastructure.Json;

/// <summary>
/// Cached JsonSerializerOptions instances for performance.
/// Creating JsonSerializerOptions is expensive - these should be reused.
/// </summary>
public static class SharedJsonOptions
{
    /// <summary>
    /// Default options with case-insensitive property names.
    /// Use for deserializing API responses and external data.
    /// </summary>
    public static JsonSerializerOptions CaseInsensitive { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Options with camelCase naming for API responses.
    /// Use for serializing data to send to clients.
    /// </summary>
    public static JsonSerializerOptions CamelCase { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Options with indented output for debugging/logging.
    /// Use sparingly - indentation adds overhead.
    /// </summary>
    public static JsonSerializerOptions Indented { get; } = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Options with both case-insensitive reading and indented output.
    /// </summary>
    public static JsonSerializerOptions CaseInsensitiveIndented { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>
    /// Web-optimized options matching ASP.NET Core defaults.
    /// </summary>
    public static JsonSerializerOptions Web { get; } = new(JsonSerializerDefaults.Web);
}
