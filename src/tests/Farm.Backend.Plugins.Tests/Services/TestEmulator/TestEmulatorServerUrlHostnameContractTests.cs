using System.Text.RegularExpressions;
using Farm.Backend.Plugin.TestEmulator;
using FluentAssertions;
using Xunit;

namespace Farm.Backend.Plugins.Tests.Services.TestEmulator;

/// <summary>
/// Cross-stack contract test for issue #1546. <c>TestEmulatorSeeder.BuildServerUrl</c>
/// (src/backends/Farm.Backend.Plugin.TestEmulator/TestEmulatorSeeder.cs) always creates simulated
/// printers with <c>ServerUrl = "http://testemulator-{printerId}"</c>, where <c>printerId</c> is a
/// .NET <see cref="Guid"/> in its default lowercase, dashed 8-4-4-4-12 form. The React frontend
/// (src/Web/ReactApp/src/common/utils/validation.ts, <c>INTERNAL_ONLY_HOSTNAME_PATTERNS</c>) relies
/// on that exact hostname shape to recognize the internal-only, browser-unreachable host and disable
/// the "Open in Browser" action instead of rendering a broken link.
///
/// Nothing in the type system enforces these two independently-maintained literals stay in sync, so
/// this test calls the real production method (via <c>InternalsVisibleTo</c>, see
/// Farm.Backend.Plugin.TestEmulator/AssemblyInfo.cs) rather than re-implementing its logic — if
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
        // Calls TestEmulatorSeeder's actual URL-generation logic (not a re-implementation), so a
        // change to the real prefix/format is guaranteed to flow into this assertion.
        Guid printerId = Guid.NewGuid();
        string serverUrl = TestEmulatorSeeder.BuildServerUrl(printerId);

        var uri = new Uri(serverUrl);

        FrontendInternalOnlyHostnamePattern.IsMatch(uri.Host).Should().BeTrue(
            "the frontend's isBrowserReachableUrl() must recognize every TestEmulatorSeeder-generated " +
            "hostname as internal-only, or the #1546 broken-link regression reappears silently");
    }

    [Fact]
    public void SeededServerUrl_UsesHttpSchemeAndIsParseableAsAbsoluteUri()
    {
        // Guards against a future change that breaks the assumption isBrowserReachableUrl relies on:
        // that the seeded URL is a well-formed absolute http(s) URL isSafeHttpUrl() can evaluate.
        string serverUrl = TestEmulatorSeeder.BuildServerUrl(Guid.NewGuid());

        Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri).Should().BeTrue();
        uri!.Scheme.Should().Be(Uri.UriSchemeHttp);
    }
}
