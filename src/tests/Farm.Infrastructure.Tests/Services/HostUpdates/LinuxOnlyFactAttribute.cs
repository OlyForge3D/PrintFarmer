namespace Farm.Infrastructure.Tests.Services.HostUpdates;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LinuxOnlyFactAttribute : FactAttribute
{
    public LinuxOnlyFactAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux-only: host identity storage is qualified on Linux only; other platforms fail closed (identity_storage_platform_unsupported).";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class LinuxOnlyTheoryAttribute : TheoryAttribute
{
    public LinuxOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip = "Linux-only: host identity storage is qualified on Linux only; other platforms fail closed (identity_storage_platform_unsupported).";
        }
    }
}
