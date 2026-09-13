extern alias PrinterDiscoveryRef;

using System.Reflection;
using System.Reflection.Emit;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Services.SystemStatus;
using Farm.Infrastructure.Settings;
using Farm.Modules.Calibration.Startup;
using Farm.Slicer.Module.Services.SystemInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using PrinterDiscovery = PrinterDiscoveryRef::PrinterDiscovery;

namespace Farm.Web.Api.Tests.Services.SystemInfo;

public sealed class LocalServiceInventorySourceTests
{
    [Theory]
    [InlineData("split", true)]
    [InlineData("microservices", true)]
    [InlineData("monolith", false)]
    public async Task ReadAsync_ConfiguredTopology_NeverCopiesApiBuildToExternalComponents(string mode, bool required)
    {
        Mock<ISettingsService> settings = new();
        settings.Setup(service => service.GetByKey(NetworkDiscoverySettings.SectionName))
            .Returns(new NetworkDiscoverySettings { EnableDiscovery = false });
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Deployment:Mode"] = mode }).Build();
        LocalServiceInventorySource source = new(settings.Object, typeof(Program).Assembly, CalibrationProfileResolutionStartup.IsSplitDeployment(configuration));
        IReadOnlyList<ServiceReplicaObservationDto> rows = await source.ReadAsync(CancellationToken.None);
        Assert.Equal(required, rows.Single(row => row.Component == "slicer-host").Required);
        Assert.Equal(InventoryObservationState.Unknown, rows.Single(row => row.Component == "discovery").ObservationState);
        Assert.False(rows.Single(row => row.Component == "discovery").Required);
        Assert.All(rows.Where(row => row.Component != "api"), row => Assert.Null(row.ApplicationVersion));
        Assert.All(rows, row => Assert.Null(row.PlatformDigest));
        Assert.NotNull(rows.Single(row => row.Component == "api").ApplicationVersion);
    }

    [Fact]
    public async Task ReadAsync_UsesSuppliedHostAssembly_NotInventoryImplementationAssembly()
    {
        const string version = "7.8.9";
        string commit = new('a', 40);
        AssemblyBuilder hostAssembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("InventoryTestHost"), AssemblyBuilderAccess.Run);
        hostAssembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!,
            [$"{version}+sha.{commit}"]));
        LocalServiceInventorySource source = new(Mock.Of<ISettingsService>(), hostAssembly, false);

        IReadOnlyList<ServiceReplicaObservationDto> rows = await source.ReadAsync(CancellationToken.None);

        ServiceReplicaObservationDto api = Assert.Single(rows, row => row.Component == "api");
        Assert.Equal(version, api.ApplicationVersion);
        Assert.Equal(commit, api.SourceCommit);
        Assert.Null(api.PlatformDigest);
        Assert.All(rows.Where(row => row.Component != "api"), row => Assert.Null(row.ApplicationVersion));
    }

    [Fact]
    public async Task ReadAsync_NoRegistry_DoesNotClaimNoWorkersInstalled()
    {
        SlicerServiceInventorySource source = new(null, NullLogger<SlicerServiceInventorySource>.Instance);
        IReadOnlyList<ServiceReplicaObservationDto> rows = await source.ReadAsync(CancellationToken.None);
        Assert.Equal(InventoryObservationState.Unknown, Assert.Single(rows).ObservationState);
    }

    [Fact]
    public void DiscoveryInfo_UsesDiscoveryAssemblyBuildInsteadOfPlaceholder()
    {
        var controller = new PrinterDiscovery.Controllers.DiscoveryController(
            Mock.Of<PrinterDiscovery.Services.INetworkDiscoveryService>(),
            Mock.Of<PrinterDiscovery.Services.IStreamingDiscoveryService>(),
            NullLogger<PrinterDiscovery.Controllers.DiscoveryController>.Instance);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(controller.GetServiceInfo(new ConfigurationBuilder().Build()));
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(result.Value));
        Assert.Equal(ApplicationBuildObservation.FromAssembly(typeof(PrinterDiscovery.Controllers.DiscoveryController).Assembly).Version,
            json.RootElement.GetProperty("Version").GetString());
    }
}
