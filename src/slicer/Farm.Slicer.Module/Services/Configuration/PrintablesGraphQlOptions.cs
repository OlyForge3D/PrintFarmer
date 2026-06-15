namespace Farm.Slicer.Module.Services.Configuration;

/// <summary>
/// Runtime options for Printables GraphQL integration.
/// </summary>
public sealed class PrintablesGraphQlOptions
{
    /// <summary>Configuration section key.</summary>
    public const string SectionName = "PrintablesGraphQl";

    /// <summary>GraphQL endpoint URL.</summary>
    public string Endpoint { get; set; } = "https://api.printables.com/graphql/";

    /// <summary>HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 15;

    /// <summary>In-memory cache TTL in seconds for read queries.</summary>
    public int CacheTtlSeconds { get; set; } = 300;

    /// <summary>Max attempts for transient retry (first attempt included).</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Initial retry delay in milliseconds for exponential backoff.</summary>
    public int RetryBaseDelayMs { get; set; } = 250;
}
