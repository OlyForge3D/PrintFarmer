using System.Text.Json;

namespace Farm.Importing.Services.Import;

internal static class ImportJsonOptions
{
    public static readonly JsonSerializerOptions Default = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    };
}
