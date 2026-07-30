using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Spoolman;
using FluentAssertions;

namespace Farm.Web.Api.Tests.Services.Spoolman;

public sealed class CanonicalSpoolIdentityTests
{
    [Fact]
    public void Constructor_EquivalentSourceUrls_NormalizesToSameIdentity()
    {
        CanonicalSpoolIdentity first = new(
            SpoolSourceKind.MoonrakerNative,
            "HTTP://MOON.LOCAL:80/",
            42);
        CanonicalSpoolIdentity second = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local",
            42);

        first.Should().Be(second);
        first.SourceIdentity.Should().Be("http://moon.local");
    }

    [Fact]
    public void Constructor_DifferentlyCasedPaths_RemainDistinct()
    {
        CanonicalSpoolIdentity first = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local/SpoolSource",
            42);
        CanonicalSpoolIdentity second = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon.local/spoolsource",
            42);

        first.Should().NotBe(second);
    }

    [Fact]
    public void FromPrinter_CentralAndNativeWithSameNumericId_RemainDistinct()
    {
        Printer centralPrinter = new()
        {
            Backend = (int)PrinterBackend.OctoPrint,
            ServerUrl = "http://octo.local",
        };
        Printer nativePrinter = new()
        {
            Backend = (int)PrinterBackend.Moonraker,
            ServerUrl = "http://moon.local",
        };

        CanonicalSpoolIdentity? central = CanonicalSpoolIdentity.FromPrinter(
            centralPrinter,
            42,
            "http://central.local");
        CanonicalSpoolIdentity? native = CanonicalSpoolIdentity.FromPrinter(
            nativePrinter,
            42,
            "http://central.local");

        central.Should().NotBeNull();
        native.Should().NotBeNull();
        central.Should().NotBe(native);
        central!.Value.SourceKind.Should().Be(SpoolSourceKind.Central);
        native!.Value.SourceKind.Should().Be(SpoolSourceKind.MoonrakerNative);
    }

    [Fact]
    public void RecordAuthoritativeUsage_DuplicateRetry_PreservesFirstAttribution()
    {
        CanonicalSpoolIdentity first = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon-a.local",
            42);
        CanonicalSpoolIdentity retry = new(
            SpoolSourceKind.MoonrakerNative,
            "http://moon-b.local",
            42);
        PrintJobToolheadUsage usage = new()
        {
            SpoolmanSpoolId = 42,
        };

        bool firstRecorded = usage.RecordAuthoritativeUsage(12, first);
        bool retryRecorded = usage.RecordAuthoritativeUsage(99, retry);

        firstRecorded.Should().BeTrue();
        retryRecorded.Should().BeFalse();
        usage.FilamentUsageGrams.Should().Be(12);
        usage.SpoolSourceIdentity.Should().Be("http://moon-a.local");
        usage.IsFilamentUsageAuthoritative.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void RecordAuthoritativeUsage_NonPositiveOrNonFinite_DoesNotQualify(
        double grams)
    {
        PrintJobToolheadUsage usage = new()
        {
            SpoolmanSpoolId = 42,
        };
        CanonicalSpoolIdentity identity = new(
            SpoolSourceKind.Central,
            "http://central.local",
            42);

        bool recorded = usage.RecordAuthoritativeUsage(grams, identity);

        recorded.Should().BeFalse();
        usage.IsFilamentUsageAuthoritative.Should().BeFalse();
        usage.SpoolSourceIdentity.Should().BeNull();
    }
}
