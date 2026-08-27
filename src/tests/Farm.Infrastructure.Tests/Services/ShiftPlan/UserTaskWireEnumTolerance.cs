using Farm.Infrastructure.Data.Configurations;
using Farm.Infrastructure.Domain;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.ShiftPlan;

/// <summary>
/// Verifies canonical wire values for shift-plan enums round-trip through the
/// EF converter and, critically, that unknown/future values (including empty
/// strings written by the initial migration's default) are recovered as
/// <see cref="UserTaskAnchorKind.Unspecified"/> / <see cref="UserTaskSourceKind.Unspecified"/>
/// so legacy tasks and forward-compat clients both survive.
/// </summary>
public class UserTaskWireEnumTolerance
{
    [Theory]
    [InlineData(UserTaskAnchorKind.Unspecified, "unspecified")]
    [InlineData(UserTaskAnchorKind.Now, "now")]
    [InlineData(UserTaskAnchorKind.At, "at")]
    [InlineData(UserTaskAnchorKind.Window, "window")]
    [InlineData(UserTaskAnchorKind.AnytimeToday, "anytimeToday")]
    public void AnchorKind_RoundTripsCanonicalWireValue(UserTaskAnchorKind value, string expected)
    {
        Assert.Equal(expected, UserTaskConfiguration.AnchorKindToWire(value));
        Assert.Equal(value, UserTaskConfiguration.AnchorKindFromWire(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("future-value")]
    [InlineData("NOW")]                 // wire is case-sensitive per Dallas triage
    [InlineData("something wild")]
    public void AnchorKind_UnknownWireValue_IsUnspecified(string wire)
        => Assert.Equal(UserTaskAnchorKind.Unspecified, UserTaskConfiguration.AnchorKindFromWire(wire));

    [Theory]
    [InlineData(UserTaskSourceKind.Unspecified, "unspecified")]
    [InlineData(UserTaskSourceKind.FailureIncident, "failureIncident")]
    [InlineData(UserTaskSourceKind.Harvest, "harvest")]
    [InlineData(UserTaskSourceKind.FilamentCoverage, "filamentCoverage")]
    [InlineData(UserTaskSourceKind.Maintenance, "maintenance")]
    [InlineData(UserTaskSourceKind.SpoolReorder, "spoolReorder")]
    [InlineData(UserTaskSourceKind.PrintedPartStock, "printedPartStock")]
    public void SourceKind_RoundTripsCanonicalWireValue(UserTaskSourceKind value, string expected)
    {
        Assert.Equal(expected, UserTaskConfiguration.SourceKindToWire(value));
        Assert.Equal(value, UserTaskConfiguration.SourceKindFromWire(expected));
    }

    [Theory]
    [InlineData("")]
    [InlineData("future-source")]
    public void SourceKind_UnknownWireValue_IsUnspecified(string wire)
        => Assert.Equal(UserTaskSourceKind.Unspecified, UserTaskConfiguration.SourceKindFromWire(wire));
}
