using System.Security.Cryptography;
using System.Text.Json;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Content identity, deliberately independent of observation and operation fences.</summary>
public static class PrinterFileEnrichmentIdentity
{
    public static string For(PrinterFileLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            location.SourcePrinterId,
            location.StorageRoot,
            location.RelativePath,
            location.ConfigurationGeneration,
            location.SizeBytes,
            location.ModifiedValue,
            location.ProviderVersion,
            location.ProviderDigest,
        })));
    }
}
