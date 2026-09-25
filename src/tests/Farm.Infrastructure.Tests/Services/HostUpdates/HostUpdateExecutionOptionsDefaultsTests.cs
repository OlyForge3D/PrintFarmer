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
