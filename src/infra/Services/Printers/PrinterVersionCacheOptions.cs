namespace Farm.Infrastructure.Services.Printers;

public sealed class PrinterVersionCacheOptions
{
    public TimeSpan Ttl { get; set; } = TimeSpan.FromMinutes(10);
}
