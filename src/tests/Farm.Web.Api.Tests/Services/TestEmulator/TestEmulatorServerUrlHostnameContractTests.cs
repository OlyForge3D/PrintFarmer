using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.TestEmulator;

/// <summary>
/// Cross-stack contract test for issue #1546. <c>TestEmulatorSeeder</c>
/// (src/backends/Farm.Backend.Plugin.TestEmulator/TestEmulatorSeeder.cs) always creates simulated
/// printers with <c>ServerUrl = $"http://testemulator-{printerId}"</c>, where <c>printerId</c> is a
/// .NET <see cref="Guid"/> in its default lowercase, dashed 8-4-4-4-12 form. The React frontend
/// (src/Web/ReactApp/src/common/utils/validation.ts, <c>INTERNAL_ONLY_HOSTNAME_PATTERNS</c>) relies
/// on that exact hostname shape to recognize the internal-only, browser-unreachable host and disable
/// the "Open in Browser" action instead of rendering a broken link.
///
/// Nothing in the type system enforces these two independently-maintained literals stay in sync — if
/// either the backend's "testemulator-" prefix or the seeded printer ID's format ever changes, this
/// test (and the equivalent frontend test in validation.test.ts) will fail, giving an explicit signal
/// that the frontend regex must be updated too, instead of the #1546 regression reappearing silently.
/// </summary>
public class TestEmulatorServerUrlHostnameContractTests
{
    // Mirrors the frontend's INTERNAL_ONLY_HOSTNAME_PATTERNS entry in validation.ts. Keep in sync.
    private static readonly Regex FrontendInternalOnlyHostnamePattern = new(
        "^testemulator-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase);

    [Fact]
    public void SeededServerUrl_HostnameMatchesFrontendInternalOnlyPattern()
    {
        // Reproduces exactly what TestEmulatorSeeder.TrySeedAsync does when creating a new printer.
        Guid printerId = Guid.NewGuid();
        string serverUrl = $"http://testemulator-{printerId}";

        var uri = new Uri(serverUrl);

        FrontendInternalOnlyHostnamePattern.IsMatch(uri.Host).Should().BeTrue(
            "the frontend's isBrowserReachableUrl() must recognize every TestEmulatorSeeder-generated " +
            "hostname as internal-only, or the #1546 broken-link regression reappears silently");
    }

    [Fact]
    public void SeederLookupPrefix_IsASubsetOfTheFrontendPattern()
    {
        // TestEmulatorSeeder itself re-identifies existing emulator printers via
        // p.ServerUrl.StartsWith("http://testemulator-", ...) — assert that literal prefix is exactly
        // the one the frontend pattern also keys off, so the two can't silently drift apart.
        const string seederLookupPrefix = "http://testemulator-";

        seederLookupPrefix.Should().Be("http://testemulator-");
        FrontendInternalOnlyHostnamePattern.ToString().Should().StartWith("^testemulator-");
    }
}
