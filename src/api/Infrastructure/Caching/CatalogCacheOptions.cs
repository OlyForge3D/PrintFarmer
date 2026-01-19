namespace Farm.Web.Api.Infrastructure.Caching;

public sealed class CatalogCacheOptions
{
    /// <summary>TTL for manufacturer and model list cache entries. Default 2 minutes.</summary>
    public TimeSpan ListTtl { get; set; } = TimeSpan.FromMinutes(2);
}
