namespace Farm.HostUpdate.Cli.Tests;

/// <summary>
/// A fact that needs a readable standing policy. Host-state ownership validation supports only
/// Windows and Linux, so elsewhere the policy is unverifiable by design and the case is skipped;
/// the portable <c>policy_unverifiable</c> behaviour is covered with host state disabled.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HostStateFactAttribute : FactAttribute
{
    public HostStateFactAttribute()
    {
        if (!CliHostFixture.HostStateSupported)
        {
            Skip = "Host-state policy ownership validation supports only Windows and Linux.";
        }
    }
}

/// <summary>The theory counterpart of <see cref="HostStateFactAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class HostStateTheoryAttribute : TheoryAttribute
{
    public HostStateTheoryAttribute()
    {
        if (!CliHostFixture.HostStateSupported)
        {
            Skip = "Host-state policy ownership validation supports only Windows and Linux.";
        }
    }
}
