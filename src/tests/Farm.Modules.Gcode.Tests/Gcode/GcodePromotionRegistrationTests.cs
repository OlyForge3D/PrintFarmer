using Farm.Modules.Gcode.Services.Gcode;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Modules.Gcode.Tests.Gcode;

/// <summary>Guards explicit-only promotion registration.</summary>
public sealed class GcodePromotionRegistrationTests
{
    [Fact]
    public void ConfigureServices_RegistersReconciliationWithoutPromotionCandidateScanner()
    {
        var services = new ServiceCollection();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        new GcodeApiModule().ConfigureServices(services, configuration);

        Type[] typedPromotionHostedServices = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) &&
                descriptor.ImplementationType?.Name.Contains(
                    "Promotion",
                    StringComparison.Ordinal) == true)
            .Select(descriptor => descriptor.ImplementationType!)
            .ToArray();
        typedPromotionHostedServices.Should().Equal(typeof(GcodePromotionReconciliationService));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(ISliceArtifactLibraryService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
    }
}
