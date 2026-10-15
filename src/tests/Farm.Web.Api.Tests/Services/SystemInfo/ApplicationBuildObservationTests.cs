extern alias PrinterDiscoveryRef;

using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using Farm.Infrastructure.Services.SystemStatus;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using PrinterDiscovery = PrinterDiscoveryRef::PrinterDiscovery;

namespace Farm.Web.Api.Tests.Services.SystemInfo;

public sealed class ApplicationBuildObservationTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("0.0.0", null)]
    [InlineData("latest", null)]
    [InlineData("C:/private/build", null)]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3-beta.7", "1.2.3-beta.7")]
    public void Parse_MissingOrLegacyBuild_NeverFabricatesVersion(string? input, string? expected)
    {
        Assert.Equal(expected, ApplicationBuildObservation.Parse(input).Version);
        Assert.Null(ApplicationBuildObservation.Parse(input).Commit);
    }

    [Fact]
    public void Parse_CanonicalBuildMarker_RetainsFullSha()
    {
        string commit = new('a', 40);
        Assert.Equal(("1.2.3-insider.10", commit), ApplicationBuildObservation.Parse($"1.2.3-insider.10+sha.{commit}"));
    }

    [Fact]
    public void FromAssembly_SuppliedHostAssembly_PreservesHostBuild()
    {
        string commit = new('a', 40);
        AssemblyBuilder hostAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("BuildTestHost"), AssemblyBuilderAccess.Run);
        hostAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [$"7.8.9+sha.{commit}"]));
        Assert.Equal(("7.8.9", commit), ApplicationBuildObservation.FromAssembly(hostAssembly));
    }

    [Fact]
    public void DiscoveryInfo_DiscoveryAssembly_PreservesApplicationVersion()
    {
        var controller = new PrinterDiscovery.Controllers.DiscoveryController(
            Mock.Of<PrinterDiscovery.Services.INetworkDiscoveryService>(),
            Mock.Of<PrinterDiscovery.Services.IStreamingDiscoveryService>(),
            NullLogger<PrinterDiscovery.Controllers.DiscoveryController>.Instance);
        var result = Assert.IsType<OkObjectResult>(controller.GetServiceInfo(new ConfigurationBuilder().Build()));
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(ApplicationBuildObservation.FromAssembly(typeof(PrinterDiscovery.Controllers.DiscoveryController).Assembly).Version,
            json.RootElement.GetProperty("Version").GetString());
    }
}
