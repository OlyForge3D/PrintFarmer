using System.Reflection;
using Farm.Web.Api.Controllers;
using Farm.Web.Api.Controllers.Responses;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace Farm.Web.Api.Tests.Controllers;

public sealed class SystemSourceControllerTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void GetSource_ValidReleaseMetadata_ReturnsExactCommitLinks()
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = Revision,
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        OkObjectResult result = actionResult.Result.Should().BeOfType<OkObjectResult>().Subject;
        SourceInfoResponse response = result.Value.Should().BeOfType<SourceInfoResponse>().Subject;
        response.License.Should().Be("AGPL-3.0-only");
        response.SourceAvailable.Should().BeTrue();
        response.Revision.Should().Be(Revision);
        response.SourceUrl.Should().Be($"https://github.com/OlyForge3D/PrintFarmer/tree/{Revision}");
        response.SourceArchiveUrl.Should().Be(
            "https://github.com/OlyForge3D/PrintFarmer/releases/download/v0.2.3/PrintFarmer-v0.2.3-source.tar.gz");
        response.SbomUrl.Should().Be(
            "https://github.com/OlyForge3D/PrintFarmer/releases/download/v0.2.3/printfarmer-v0.2.3.spdx.json");
    }

    [Fact]
    public void GetSource_MissingRevision_ReturnsUnavailableWithoutVersionedLinks()
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = "development",
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceAvailable.Should().BeFalse();
        response.Revision.Should().BeNull();
        response.SourceUrl.Should().BeNull();
        response.SourceArchiveUrl.Should().BeNull();
        response.LicenseUrl.Should().BeNull();
        response.NoticesUrl.Should().BeNull();
        response.SbomUrl.Should().BeNull();
    }

    [Fact]
    public void GetSource_PrivateMetadataUrls_ReturnsUnavailableWithoutFallbackLinks()
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = Revision,
            ["SourceInfo:RepositoryUrl"] = "http://10.0.0.4/private",
            ["SourceInfo:SourceArchiveUrl"] = "https://build.internal/source.tar.gz",
            ["SourceInfo:SbomUrl"] = "https://192.168.1.10/sbom.json",
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceAvailable.Should().BeFalse();
        response.RepositoryUrl.Should().BeNull();
        response.SourceUrl.Should().BeNull();
        response.SourceArchiveUrl.Should().BeNull();
        response.SbomUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("https://10.0.0.4/sbom.json")]
    [InlineData("https://[fc00::1]/sbom.json")]
    [InlineData("https://[::ffff:192.168.1.10]/sbom.json")]
    [InlineData("https://[ff02::1]/sbom.json")]
    public void GetSource_NonPublicSbomUrl_DoesNotSubstituteAnotherArtifact(string sbomUrl)
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = Revision,
            ["SourceInfo:SbomUrl"] = sbomUrl,
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceAvailable.Should().BeTrue();
        response.SbomUrl.Should().BeNull();
    }

    [Theory]
    [InlineData("https://downloads.example.com/latest-v0.2.3/source.tar.gz")]
    [InlineData("https://downloads.example.com/main/v0.2.3/source.tar.gz")]
    [InlineData("https://downloads.example.com/releases/v0.2.3/source.tar.gz?channel=latest")]
    [InlineData("https://downloads.example.com/releases/v0.2.3/source.tar.gz#current")]
    [InlineData("https://downloads.example.com/releases/v0.2.30/source.tar.gz")]
    [InlineData("https://downloads.example.com/releases/v0.2.3.1/source.tar.gz")]
    [InlineData("https://downloads.example.com/releases/v0.2.3-beta.1/source.tar.gz")]
    public void GetSource_MutableArtifactUrls_AreSuppressed(string artifactUrl)
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = Revision,
            ["SourceInfo:SourceArchiveUrl"] = artifactUrl,
            ["SourceInfo:SbomUrl"] = artifactUrl,
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceAvailable.Should().BeTrue();
        response.SourceArchiveUrl.Should().BeNull();
        response.SbomUrl.Should().BeNull();
    }

    [Fact]
    public void GetSource_CustomImmutableArtifactUrls_AreReturned()
    {
        string sourceArchiveUrl =
            "https://downloads.example.com/releases/v0.2.3/PrintFarmer-v0.2.3-source.tar.gz";
        string sbomUrl = $"https://downloads.example.com/objects/{Revision}/printfarmer.spdx.json";
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v0.2.3",
            ["SourceInfo:Revision"] = Revision,
            ["SourceInfo:SourceArchiveUrl"] = sourceArchiveUrl,
            ["SourceInfo:SbomUrl"] = sbomUrl,
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceArchiveUrl.Should().Be(sourceArchiveUrl);
        response.SbomUrl.Should().Be(sbomUrl);
    }

    [Fact]
    public void GetSource_CustomRepositoryWithoutArtifacts_ReturnsOnlyExactSourceTree()
    {
        IConfiguration configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["SourceInfo:Version"] = "v1.0.0",
            ["SourceInfo:Revision"] = Revision,
            ["SourceInfo:RepositoryUrl"] = "https://github.com/example/custom-printfarmer",
        });
        var controller = new SystemSourceController(configuration);

        ActionResult<SourceInfoResponse> actionResult = controller.GetSource();

        SourceInfoResponse response = actionResult.Result
            .Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<SourceInfoResponse>().Subject;
        response.SourceAvailable.Should().BeTrue();
        response.SourceUrl.Should().Be($"https://github.com/example/custom-printfarmer/tree/{Revision}");
        response.SourceArchiveUrl.Should().BeNull();
        response.SbomUrl.Should().BeNull();
    }

    [Fact]
    public void GetSource_EndpointMethod_AllowsAnonymousNetworkUsers()
    {
        MethodInfo method = typeof(SystemSourceController).GetMethod(nameof(SystemSourceController.GetSource))!;

        method.GetCustomAttribute<AllowAnonymousAttribute>().Should().NotBeNull();
    }

    [Fact]
    public void AssemblyMetadata_BuildOutput_DeclaresAgplAndRepository()
    {
        Dictionary<string, string?> metadata = typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(attribute => attribute.Key, attribute => attribute.Value);

        metadata["License"].Should().Be("AGPL-3.0-only");
        metadata["RepositoryUrl"].Should().Be("https://github.com/OlyForge3D/PrintFarmer");
    }

    private static IConfiguration CreateConfiguration(Dictionary<string, string?> values)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
