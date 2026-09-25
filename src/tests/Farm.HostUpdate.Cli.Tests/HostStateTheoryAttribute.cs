namespace Farm.HostUpdate.Cli.Tests;

/// <summary>The <see cref="HostStateFactAttribute"/> equivalent for data-driven cases.</summary>
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
