namespace Farm.Web.Api.Infrastructure.Caching;

internal sealed class PrinterVersionCacheOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);
}
