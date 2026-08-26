using Farm.Infrastructure.PrinterCalibration;
using Farm.Web.Api.Services.Calibration;
using Farm.Web.Api.Startup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Farm.Web.Api.Tests.Calibration;

/// <summary>
/// Deployment-topology selection for the calibration profile resolver. A monolith must keep its
/// local database-backed resolver; a split host must get the authenticated HTTP adapter and must
/// fail closed (or fail fast) rather than guess an internal address.
/// </summary>
public sealed class CalibrationProfileResolutionStartupTests
{
    private const string SlicerHostUrl = "http://slicer-host:5246";

    [Theory]
    [InlineData("split")]
    [InlineData("Split")]
    [InlineData("microservices")]
    [InlineData("MICROSERVICES")]
    public void AddCalibrationProfileResolution_ForSplitDeployments_RegistersTheHttpAdapter(
        string deploymentMode)
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_MODE"] = deploymentMode,
            ["SlicerHost:BaseUrl"] = SlicerHostUrl,
        });

        using (provider)
        {
            ICalibrationProfileResolver? resolver =
                provider.GetService<ICalibrationProfileResolver>();

            _ = resolver.Should().BeOfType<SlicerHostCalibrationProfileResolver>();
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("monolith")]
    [InlineData("standalone")]
    public void AddCalibrationProfileResolution_ForMonolithDeployments_PreservesTheLocalResolver(
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

        ServiceProvider provider = BuildProvider(settings, registerLocalResolver: true);

        using (provider)
        {
            ICalibrationProfileResolver? resolver =
                provider.GetService<ICalibrationProfileResolver>();

            _ = resolver.Should().BeOfType<LocalTestCalibrationProfileResolver>(
                "the monolith resolves profiles from its in-process slicer database");
        }
    }

    [Fact]
    public void AddCalibrationProfileResolution_ForDeploymentTypeMicroservices_RegistersTheHttpAdapter()
    {
        // The deploy installers write DEPLOYMENT_TYPE, and Program.cs accepts it as a synonym when
        // deciding to skip slicer integration. The resolver gate must not disagree with that gate.
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_TYPE"] = "microservices",
            ["SlicerHost:BaseUrl"] = SlicerHostUrl,
        });

        using (provider)
        {
            _ = provider.GetService<ICalibrationProfileResolver>()
                .Should().BeOfType<SlicerHostCalibrationProfileResolver>();
        }
    }

    [Fact]
    public void AddCalibrationProfileResolution_ForDeploymentTypeSplit_PreservesTheLocalResolver()
    {
        // Program.cs only treats DEPLOYMENT_TYPE=microservices as "no in-process slicer module", so
        // DEPLOYMENT_TYPE=split still loads the module and must keep its database resolver.
        ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["DEPLOYMENT_TYPE"] = "split",
                ["SlicerHost:BaseUrl"] = SlicerHostUrl,
            },
            registerLocalResolver: true);

        using (provider)
        {
            _ = provider.GetService<ICalibrationProfileResolver>()
                .Should().BeOfType<LocalTestCalibrationProfileResolver>();
        }
    }

    [Fact]
    public void AddCalibrationProfileResolution_ForSplitDeploymentWithoutConfiguredUrl_RegistersNothing()
    {
        ServiceProvider provider = BuildProvider(new Dictionary<string, string?>
        {
            ["DEPLOYMENT_MODE"] = "split",
        });

        using (provider)
        {
            _ = provider.GetService<ICalibrationProfileResolver>().Should().BeNull(
                "an unconfigured hop must stay unavailable rather than guess an internal address");
        }
    }

    [Theory]
    [InlineData("slicer-host:5246")]
    [InlineData("ftp://slicer-host:5246")]
    [InlineData("http://slicer-host:5246/?token=abc")]
    [InlineData("http://slicer-host:5246/#fragment")]
    [InlineData("http://user:pass@slicer-host:5246")]
    [InlineData("   ")]
    public void AddCalibrationProfileResolution_WithInvalidUrl_FailsFast(string baseUrl)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "split",
                ["SlicerHost:BaseUrl"] = baseUrl,
            })
            .Build();
        ServiceCollection services = new();

        Action register = () => services.AddCalibrationProfileResolution(configuration);

        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Blank is "not configured", which stays fail-closed instead of crashing the host.
            register.Should().NotThrow();
            _ = services.Should().NotContain(descriptor =>
                descriptor.ServiceType == typeof(ICalibrationProfileResolver));
        }
        else
        {
            _ = register.Should().Throw<InvalidOperationException>();
        }
    }

    [Theory]
    [InlineData("ResolveTimeoutSeconds", "0")]
    [InlineData("ResolveTimeoutSeconds", "600")]
    [InlineData("HealthTimeoutSeconds", "0")]
    [InlineData("MaxResponseBytes", "8")]
    public void AddCalibrationProfileResolution_WithOutOfRangeBounds_FailsFast(string key, string value)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEPLOYMENT_MODE"] = "split",
                ["SlicerHost:BaseUrl"] = SlicerHostUrl,
                [$"SlicerHost:{key}"] = value,
            })
            .Build();
        ServiceCollection services = new();

        Action register = () => services.AddCalibrationProfileResolution(configuration);

        _ = register.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryCreate_NormalisesTheBaseUrlForRelativeRouteResolution()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SlicerHost:BaseUrl"] = "http://slicer-host:5246",
            })
            .Build();

        bool created = SlicerHostCalibrationResolverOptions.TryCreate(
            configuration,
            out SlicerHostCalibrationResolverOptions? options,
            out string? error);

        _ = created.Should().BeTrue();
        _ = error.Should().BeNull();
        _ = options!.BaseUrl.ToString().Should().EndWith("/");
        _ = new Uri(options.BaseUrl, CalibrationProfileResolutionContract.ResolveRelativeRoute)
            .AbsolutePath.Should().Be("/" + CalibrationProfileResolutionContract.ResolveRelativeRoute);
        _ = new Uri(options.BaseUrl, CalibrationProfileResolutionContract.HealthRelativeRoute)
            .AbsolutePath.Should().Be("/" + CalibrationProfileResolutionContract.HealthRelativeRoute);
    }

    [Fact]
    public void ComposeDefaultBaseUrl_MatchesTheGeneratedComposeService()
    {
        _ = SlicerHostCalibrationResolverOptions.ComposeDefaultBaseUrl
            .Should().Be("http://slicer-host:5246");
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings,
        bool registerLocalResolver = false)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = new();
        _ = services.AddSingleton(NullLoggerFactory.Instance);
        _ = services.AddLogging();
        if (registerLocalResolver)
        {
            _ = services.AddScoped<ICalibrationProfileResolver, LocalTestCalibrationProfileResolver>();
        }

        _ = services.AddCalibrationProfileResolution(configuration);
        return services.BuildServiceProvider();
    }

    /// <summary>Stands in for the slicer module's in-process database resolver.</summary>
    private sealed class LocalTestCalibrationProfileResolver : ICalibrationProfileResolver
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<ResolvedCalibrationProfiles> ResolveAsync(
            Guid machineProfileId,
            Guid processProfileId,
            Guid filamentProfileId,
            CalibrationProfileAccessScope accessScope,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ResolvedCalibrationProfiles(null, null, null));
    }
}
