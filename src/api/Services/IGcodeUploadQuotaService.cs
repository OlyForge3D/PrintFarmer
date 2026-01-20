namespace Farm.Web.Api.Services;

public interface IGcodeUploadQuotaService
{
    bool TryAddUsage(string userId, long bytes, out long usedBytes, out long limitBytes);
}
