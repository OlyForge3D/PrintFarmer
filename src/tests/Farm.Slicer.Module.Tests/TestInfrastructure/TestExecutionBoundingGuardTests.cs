using System.Xml.Linq;

namespace Farm.Slicer.Module.Tests.TestInfrastructure;

/// <summary>
/// Guards the bounded-test-execution safety net added for issue #2013 (full .NET solution test
/// does not complete on macOS). On a macOS QA host, <c>Farm.Slicer.Module.Tests</c>' testhost
/// remained hung for 4+ hours with no output after <c>Farm.Web.Api.Tests</c> completed with
/// known environment-shaped failures; the hang was not reproducible on Windows/Linux, so
/// <c>src/vstest.runsettings</c> plus the documented <c>--blame-hang-timeout</c> flag bound the
/// documented full-solution <c>dotnet test</c> command instead of leaving it able to hang
/// indefinitely again. These tests assert the guard file still exists with a sane, bounded
/// timeout so a future edit cannot silently widen or remove it without a visible test failure.
/// </summary>
public class TestExecutionBoundingGuardTests
{
    /// <summary>
    /// Upper bound (ms) a reasonable full-solution session timeout should stay under. Normal
    /// full-suite duration observed in CI and on Windows/Linux hosts is well under 30 minutes;
    /// this generously allows up to 2 hours so the guard never becomes a source of flakiness,
    /// while still ruling out the "hours-long hang" behavior the issue reported.
    /// </summary>
    private const long MaxReasonableSessionTimeoutMs = 2 * 60 * 60 * 1000L;

    private static string FindRunSettingsPath()
    {
        string? current = AppContext.BaseDirectory;
        while (current is not null)
        {
            string candidate = Path.Join(current, "vstest.runsettings");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(Path.Join(current, "farm-web.sln")))
            {
                // We reached the solution root without finding the file; stop searching upward.
                break;
            }

            DirectoryInfo? parent = Directory.GetParent(current);
            if (parent == null)
            {
                break;
            }

            current = parent.FullName;
        }

        throw new FileNotFoundException(
            "Expected to find 'vstest.runsettings' next to farm-web.sln (src/vstest.runsettings). " +
            "This file bounds the documented full-solution 'dotnet test' run so it can never hang " +
            "indefinitely (issue #2013); it must not be removed without replacing this guard.");
    }

    [Fact]
    public void RunSettings_Exists_NextToSolution()
    {
        string path = FindRunSettingsPath();
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void RunSettings_DeclaresBoundedTestSessionTimeout()
    {
        string path = FindRunSettingsPath();
        XDocument document = XDocument.Load(path);

        XElement? timeoutElement = document.Root?.Element("RunConfiguration")?.Element("TestSessionTimeout");
        timeoutElement.Should().NotBeNull(
            "the documented full-solution dotnet test command relies on RunConfiguration/TestSessionTimeout " +
            "to bound the entire run (issue #2013)");

        bool parsed = long.TryParse(timeoutElement!.Value, out long timeoutMs);
        parsed.Should().BeTrue("TestSessionTimeout must be a valid millisecond value");
        timeoutMs.Should().BeGreaterThan(0, "a non-positive timeout would not bound anything");
        timeoutMs.Should().BeLessThanOrEqualTo(
            MaxReasonableSessionTimeoutMs,
            "the whole point of this guard file is to prevent an unbounded/hours-long hang (issue #2013); " +
            "a timeout above this ceiling defeats that purpose");
    }
}
