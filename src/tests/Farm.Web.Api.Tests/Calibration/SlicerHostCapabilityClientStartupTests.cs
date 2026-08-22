using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration.Generation;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Deployment-topology selection for the split-deployment worker-compatibility client (issue #1848).
/// A monolith host never registers the client at all, since <c>CalibrationGenerationCapabilityProbe</c>
/// reads its own local <c>IDbContextFactory&lt;SlicerDbContext&gt;</c> directly; a split host must get
/// the authenticated HTTP adapter and must fail closed (or fail fast) rather than guess an internal
/// address.
/// </summary>
public sealed class SlicerHostCapabilityClientStartupTests
{
    private const string SlicerHostUrl = "http://slicer-host:5246";

    [Theory]
    [InlineData("split")]
    [InlineData("Split")]
    [InlineData("microservices")]
    [InlineData("MICROSERVICES")]
    public void AddSlicerHostCapabilityClient_ForSplitDeployments_RegistersTheHttpAdapter(
        string deploymentMode)
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_MODE"] = deploymentMode,
            ["SlicerHost:BaseUrl"] = SlicerHostUrl,
        });

        using (provider)
        {
            ISlicerHostCapabilityClient? client = provider.GetService<ISlicerHostCapabilityClient>();

            _ = client.Should().BeOfType<SlicerHostCapabilityClient>();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("monolith")]
    [InlineData("standalone")]
    public void AddSlicerHostCapabilityClient_ForMonolithDeployments_RegistersNothing(
        string? deploymentMode)
    {
        Dictionary<string, string?> settings = new()
        {
            ["SlicerHost:BaseUrl"] = SlicerHostUrl,
        };
        if (deploymentMode is not null)
        {
            settings["DEPLOYMENT_MODE"] = deploymentMode;
        }

        ServiceProvider provider = BuildProvider(settings);

        using (provider)
        {
            _ = provider.GetService<ISlicerHostCapabilityClient>().Should().BeNull(
                "the monolith answers this in-process against a local IDbContextFactory<SlicerDbContext>");
        }
    }

    [Fact]
    public void AddSlicerHostCapabilityClient_ForSplitDeploymentWithoutConfiguredUrl_RegistersNothing()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_MODE"] = "split",
        });

        using (provider)
        {
            _ = provider.GetService<ISlicerHostCapabilityClient>().Should().BeNull(
                "an unconfigured hop must stay unavailable rather than guess an internal address");
        }
    }

    [Theory]
    [InlineData("slicer-host:5246")]
    [InlineData("ftp://slicer-host:5246")]
    [InlineData("http://slicer-host:5246/?token=abc")]
    [InlineData("   ")]
    public void AddSlicerHostCapabilityClient_WithInvalidUrl_MatchesProfileResolverGate(string baseUrl)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "split",
                ["SlicerHost:BaseUrl"] = baseUrl,
            })
            .Build();
        ServiceCollection services = new();

        Action register = () => services.AddSlicerHostCapabilityClient(configuration);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Blank is "not configured", which stays fail-closed instead of crashing the host.
            register.Should().NotThrow();
            _ = services.Should().NotContain(descriptor =>
                descriptor.ServiceType == typeof(ISlicerHostCapabilityClient));
        }
        else
        {
            _ = register.Should().Throw<InvalidOperationException>();
        }
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = new();
        _ = services.AddSingleton(configuration);
        _ = services.AddSingleton(NullLoggerFactory.Instance);
        _ = services.AddLogging();
        _ = services.AddSlicerHostCapabilityClient(configuration);
        return services.BuildServiceProvider();
    }
}
