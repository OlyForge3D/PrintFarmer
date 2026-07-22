using Farm.Web.Api.Health;
using Xunit;

namespace Farm.Web.Api.Tests.Health;

public class BuildVersionTests
{
    [Fact]
    public void Parse_VersionWithCommit_SplitsOnFirstPlus()
    {
        (string version, string? commit) = BuildVersion.Parse("0.2.2+de53651");

        Assert.Equal("0.2.2", version);
        Assert.Equal("de53651", commit);
    }

    [Fact]
    public void Parse_VersionWithoutCommit_ReturnsNullCommit()
    {
        (string version, string? commit) = BuildVersion.Parse("0.2.2");

        Assert.Equal("0.2.2", version);
        Assert.Null(commit);
    }

    [Fact]
    public void Parse_VersionWithTrailingPlusOnly_ReturnsNullCommit()
    {
        (string version, string? commit) = BuildVersion.Parse("0.2.2+");

        Assert.Equal("0.2.2", version);
        Assert.Null(commit);
    }

    [Fact]
    public void Parse_CommitContainingPlus_KeepsRemainderAsCommit()
    {
        (string version, string? commit) = BuildVersion.Parse("0.2.2+abc123+extra");

        Assert.Equal("0.2.2", version);
        Assert.Equal("abc123+extra", commit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_ReturnsUnknownVersionAndNullCommit(string? input)
    {
        (string version, string? commit) = BuildVersion.Parse(input);

        Assert.Equal(BuildVersion.UnknownVersion, version);
        Assert.Null(commit);
    }

    [Fact]
    public void FromAssembly_ReadsInformationalVersionFromProvidedAssembly()
    {
        // The test assembly's informational version is deterministic enough to assert
        // that a non-empty version is returned (never the unknown fallback).
        (string version, string? _) = BuildVersion.FromAssembly(typeof(BuildVersionTests).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(version));
    }
}
