using System.Reflection;
using Farm.OrcaSlicer.Worker.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.OrcaSlicer.Worker.Tests;

/// <summary>
/// Regression coverage for issue #2148: <c>OrcaProfilesService.ResolveProfileName</c> called
/// <c>document.RootElement.TryGetProperty("name", ...)</c> without first checking that the
/// parsed root was a JSON object. A profile file containing valid but non-object JSON (a bare
/// array, string, number, etc.) threw an unguarded <see cref="InvalidOperationException"/> that
/// escaped the method's <c>catch (JsonException)</c> block, crashing the caller instead of
/// falling back to the filename-derived name.
/// </summary>
public sealed class ResolveProfileNameShapeGuardTests : IDisposable
{
    private readonly string _profilesRoot =
        Path.Join(Path.GetTempPath(), $"pfarm-orca-resolve-name-{Guid.NewGuid():N}");

    public ResolveProfileNameShapeGuardTests()
    {
        Directory.CreateDirectory(_profilesRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_profilesRoot))
        {
            Directory.Delete(_profilesRoot, recursive: true);
        }
    }

    [Theory(DisplayName = "Non-object profile JSON falls back to the filename instead of throwing")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("null")]
    public void ResolveProfileName_NonObjectRoot_FallsBackToFileName(string json)
    {
        string filePath = Path.Join(_profilesRoot, "weird-profile.json");
        File.WriteAllText(filePath, json);
        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        string resolved = InvokeResolveProfileName(service, filePath);

        resolved.Should().Be("weird-profile");
    }

    [Fact(DisplayName = "A non-string \"name\" property falls back to the filename instead of throwing")]
    public void ResolveProfileName_NonStringNameProperty_FallsBackToFileName()
    {
        string filePath = Path.Join(_profilesRoot, "numeric-name.json");
        File.WriteAllText(filePath, """{"name": 12345}""");
        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        string resolved = InvokeResolveProfileName(service, filePath);

        resolved.Should().Be("numeric-name");
    }

    [Fact(DisplayName = "A well-formed profile still resolves its declared name with the guard in place")]
    public void ResolveProfileName_ValidObjectWithStringName_ReturnsDeclaredName()
    {
        string filePath = Path.Join(_profilesRoot, "well-formed.json");
        File.WriteAllText(filePath, """{"name": "My Profile"}""");
        var service = new OrcaProfilesService(NullLogger.Instance, _profilesRoot);

        string resolved = InvokeResolveProfileName(service, filePath);

        resolved.Should().Be("My Profile");
    }

    private static string InvokeResolveProfileName(OrcaProfilesService service, string filePath)
    {
        MethodInfo method = typeof(OrcaProfilesService).GetMethod(
            "ResolveProfileName",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ResolveProfileName is missing.");

        return (string)method.Invoke(service, [filePath])!;
    }
}
