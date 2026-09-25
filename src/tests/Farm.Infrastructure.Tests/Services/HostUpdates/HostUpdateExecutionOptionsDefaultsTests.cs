using Farm.Infrastructure.Services.HostUpdates;
using Farm.Infrastructure.Services.Queue;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

public class HostUpdateExecutionOptionsDefaultsTests
{
    [Fact]
    public void FenceProofDefaults_AreExpectedAndCoverRequiredWriterDuration()
    {
        var options = new HostUpdateExecutionOptions();

        options.FenceProofTimeoutSeconds.Should().Be(321);
        options.FencePollIntervalSeconds.Should().Be(2);
        BackendStartCommandConsumerService.RequiredFenceProofDuration
            .Should().Be(TimeSpan.FromSeconds(319));
        TimeSpan.FromSeconds(
                options.FenceProofTimeoutSeconds - options.FencePollIntervalSeconds)
            .Should().BeGreaterThanOrEqualTo(
                BackendStartCommandConsumerService.RequiredFenceProofDuration);
    }

    [Fact]
    public void ConfiguredComposeFiles_ReplaceBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeFiles:0"] = "/opt/printfarmer/docker-compose.yml",
            ["HostUpdateExecution:ComposeFiles:1"] = "/opt/printfarmer/docker-compose.override.yml",
        });

        options.ComposeFiles.Should().Equal(
            "/opt/printfarmer/docker-compose.yml",
            "/opt/printfarmer/docker-compose.override.yml");
    }

    [Fact]
    public void UnconfiguredComposeFiles_KeepBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeProjectName"] = "printfarmer",
        });

        options.ComposeFiles.Should().Equal(new HostUpdateExecutionOptions().ComposeFiles);
    }

    [Fact]
    public void ConfiguredActiveServiceIds_ReplaceBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ActiveServiceIds:0"] = "monolith",
        });

        options.ActiveServiceIds.Should().Equal("monolith");
    }

    [Fact]
    public void UnconfiguredActiveServiceIds_KeepBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeProjectName"] = "printfarmer",
        });

        options.ActiveServiceIds.Should().Equal(new HostUpdateExecutionOptions().ActiveServiceIds);
    }

    [Fact]
    public void ConfiguredServiceMappings_ReplaceBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ServiceMappings:0:ServiceId"] = "monolith",
            ["HostUpdateExecution:ServiceMappings:0:ComposeServiceName"] = "printfarmer",
            ["HostUpdateExecution:ServiceMappings:0:ImageEnvironmentVariable"] = "PRINTFARMER_IMAGE",
            ["HostUpdateExecution:ServiceMappings:0:ImageRepository"] = "ghcr.io/olyforge3d/printfarmer-monolith",
        });

        options.ServiceMappings.Should().ContainSingle().Which.Should().BeEquivalentTo(new HostUpdateServiceMappingOptions(
            "monolith",
            "printfarmer",
            "PRINTFARMER_IMAGE",
            "ghcr.io/olyforge3d/printfarmer-monolith"));
    }

    [Fact]
    public void UnconfiguredServiceMappings_KeepBuiltInDefault()
    {
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeProjectName"] = "printfarmer",
        });

        options.ServiceMappings.Should().BeEquivalentTo(
            new HostUpdateExecutionOptions().ServiceMappings,
            config => config.WithStrictOrdering());
    }

    [Fact]
    public void PartialServiceMappingOverride_IsNotMergedAndFailsValidation()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = root,
            ["HostUpdateExecution:ServiceMappings:0:ImageRepository"] = "registry.example/printfarmer-api",
        });

        options.ServiceMappings.Should().ContainSingle();
        ValidateOptionsResult result = new HostUpdateExecutionOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("is missing a required field");
    }

    [Fact]
    public void ReplacedServiceMappingsMissingAnActiveService_FailsValidatedOptionsResolution()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        using ServiceProvider provider = BuildRegistrationProvider(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = root,
            ["HostUpdateExecution:ServiceMappings:0:ServiceId"] = "monolith",
            ["HostUpdateExecution:ServiceMappings:0:ComposeServiceName"] = "printfarmer",
            ["HostUpdateExecution:ServiceMappings:0:ImageEnvironmentVariable"] = "PRINTFARMER_IMAGE",
            ["HostUpdateExecution:ServiceMappings:0:ImageRepository"] = "ghcr.io/olyforge3d/printfarmer-monolith",
        });

        Action resolve = () => _ = provider.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>()
            .WithMessage("*ActiveServiceIds must each have a ServiceMappings entry; unmapped: api,frontend,slicer-host,printer-discovery,orcaslicer-worker*");
    }

    [Fact]
    public void ReplacedServiceMappingsCoveringActiveServices_PassValidatedOptionsResolution()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        using ServiceProvider provider = BuildRegistrationProvider(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = root,
            ["HostUpdateExecution:ActiveServiceIds:0"] = "monolith",
            ["HostUpdateExecution:ServiceMappings:0:ServiceId"] = "monolith",
            ["HostUpdateExecution:ServiceMappings:0:ComposeServiceName"] = "printfarmer",
            ["HostUpdateExecution:ServiceMappings:0:ImageEnvironmentVariable"] = "PRINTFARMER_IMAGE",
            ["HostUpdateExecution:ServiceMappings:0:ImageRepository"] = "ghcr.io/olyforge3d/printfarmer-monolith",
        });

        HostUpdateExecutionOptions options = provider.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value;

        options.ActiveServiceIds.Should().Equal("monolith");
        options.ServiceMappings.Should().ContainSingle().Which.ServiceId.Should().Be("monolith");
    }

    private static ServiceProvider BuildRegistrationProvider(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddHostUpdateRecoveryEngine(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DuplicateConfiguredServiceMapping_FailsValidatedOptionsResolution()
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        var values = new Dictionary<string, string?> { ["HostUpdateExecution:RootDirectory"] = root };
        for (int index = 0; index < 2; index++)
        {
            values[$"HostUpdateExecution:ServiceMappings:{index}:ServiceId"] = "api";
            values[$"HostUpdateExecution:ServiceMappings:{index}:ComposeServiceName"] = "api";
            values[$"HostUpdateExecution:ServiceMappings:{index}:ImageEnvironmentVariable"] = "PRINTFARMER_API_IMAGE";
            values[$"HostUpdateExecution:ServiceMappings:{index}:ImageRepository"] = "ghcr.io/olyforge3d/printfarmer-api";
        }

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddHostUpdateRecoveryEngine(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Action resolve = () => _ = provider.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage("*duplicate ServiceId: api*");
    }

    [Theory]
    [InlineData("ActiveServiceIds", "ActiveServiceIds must list at least one active service")]
    [InlineData("ComposeFiles", "ComposeFiles must list at least one compose file")]
    [InlineData("ServiceMappings", "ServiceMappings must map at least one service")]
    public void ExplicitlyEmptyTopologyList_FailsValidatedOptionsResolution(string key, string expectedFailure)
    {
        // JSON "[]" and an empty environment variable both surface as a present, empty-string value.
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = root,
            [$"HostUpdateExecution:{key}"] = string.Empty,
        }).Build();
        var services = new ServiceCollection();
        services.AddHostUpdateRecoveryEngine(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        Action resolve = () => _ = provider.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().WithMessage($"*{expectedFailure}*");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ConfiguredBlankActiveServiceId_FailsValidationThroughRegistration(string entry)
    {
        string root = Path.Combine(Path.GetPathRoot(Path.GetTempPath()) ?? "C:\\", "printfarmer-host-updates-test-root");
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:RootDirectory"] = root,
            ["HostUpdateExecution:ActiveServiceIds:0"] = "monolith",
            ["HostUpdateExecution:ActiveServiceIds:1"] = entry,
        });

        options.ActiveServiceIds.Should().HaveCount(2);
        ValidateOptionsResult result = new HostUpdateExecutionOptionsValidator().Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("ActiveServiceIds entries must not be empty");
    }

    [Fact]
    public void ConfiguredSafetyLists_StayAdditiveToBuiltInDefaults()
    {
        var defaults = new HostUpdateExecutionOptions();
        HostUpdateExecutionOptions options = BindThroughRegistration(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:SupportedProviderNames:0"] = "Extra.Provider",
            ["HostUpdateExecution:RequiredAggregateHealthResultNames:0"] = "extra-health",
            ["HostUpdateExecution:RequiredFencedWriterNames:0"] = "extra-writer",
        });

        string[] expectedProviders = [.. defaults.SupportedProviderNames, "Extra.Provider"];
        string[] expectedHealth = [.. defaults.RequiredAggregateHealthResultNames, "extra-health"];
        string[] expectedWriters = [.. defaults.RequiredFencedWriterNames, "extra-writer"];
        options.SupportedProviderNames.Should().Equal(expectedProviders.AsEnumerable());
        options.RequiredAggregateHealthResultNames.Should().Equal(expectedHealth.AsEnumerable());
        options.RequiredFencedWriterNames.Should().Equal(expectedWriters.AsEnumerable());
    }

    private static HostUpdateExecutionOptions BindThroughRegistration(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddHostUpdateRecoveryEngine(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        // Apply the registered configure actions directly so options validation (which needs a
        // provisioned host root) does not run.
        var options = new HostUpdateExecutionOptions();
        foreach (IConfigureOptions<HostUpdateExecutionOptions> configure in provider.GetServices<IConfigureOptions<HostUpdateExecutionOptions>>())
        {
            configure.Configure(options);
        }

        return options;
    }

    [Fact]
    public void BackendStartFenceDeadlineAddends_AreExpected()
    {
        BackendStartCommandConsumerService.IterationDeadline
            .Should().Be(TimeSpan.FromSeconds(310));
        BackendStartCommandConsumerService.CancellationCleanupDeadline
            .Should().Be(TimeSpan.FromSeconds(4));
        BackendStartCommandConsumerService.OutcomePersistenceDeadline
            .Should().Be(TimeSpan.FromSeconds(4));
        BackendStartCommandConsumerService.FenceAcknowledgementMargin
            .Should().Be(TimeSpan.FromSeconds(1));
        (BackendStartCommandConsumerService.IterationDeadline
         + BackendStartCommandConsumerService.CancellationCleanupDeadline
         + BackendStartCommandConsumerService.OutcomePersistenceDeadline
         + BackendStartCommandConsumerService.FenceAcknowledgementMargin)
            .Should().Be(BackendStartCommandConsumerService.RequiredFenceProofDuration);
    }
}
