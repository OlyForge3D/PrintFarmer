using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Infrastructure.Tests.Services.HostUpdates;

/// <summary>
/// Issue #2997: the configuration binder appends configured array elements to a non-empty code
/// default, so a deployment's own <c>ComposeFiles</c> used to be applied together with the
/// working-directory-relative default template. Configured compose files must replace the default.
/// </summary>
public class HostUpdateExecutionOptionsBindingTests
{
    private const string First = "/srv/printfarmer/docker-compose.yml";
    private const string Second = "/srv/printfarmer/docker-compose.override.yml";

    [Fact]
    public void Bind_WithoutConfiguredComposeFiles_KeepsCodeDefault()
    {
        HostUpdateExecutionOptions options = BindFrom(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeProjectName"] = "printfarmer",
        });

        options.ComposeFiles.Should().Equal(HostUpdateExecutionOptions.DefaultComposeFiles);
    }

    [Fact]
    public void Bind_WithConfiguredComposeFiles_ReplacesCodeDefault()
    {
        HostUpdateExecutionOptions options = BindFrom(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeFiles:0"] = First,
        });

        options.ComposeFiles.Should().Equal(First);
    }

    [Fact]
    public void Bind_WithSeveralConfiguredComposeFiles_PreservesConfiguredOrder()
    {
        HostUpdateExecutionOptions options = BindFrom(new Dictionary<string, string?>
        {
            ["HostUpdateExecution:ComposeFiles:0"] = First,
            ["HostUpdateExecution:ComposeFiles:1"] = Second,
        });

        options.ComposeFiles.Should().Equal(First, Second);
    }

    [Fact]
    public void Bind_DoesNotMutateTheSharedCodeDefault()
    {
        _ = BindFrom(new Dictionary<string, string?> { ["HostUpdateExecution:ComposeFiles:0"] = First });

        new HostUpdateExecutionOptions().ComposeFiles.Should().Equal(HostUpdateExecutionOptions.DefaultComposeFiles);
    }

    [Fact]
    public void SharedEngineRegistration_ResolvesOnlyTheConfiguredComposeFiles()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HostUpdateExecution:ComposeFiles:0"] = First,
                ["HostUpdateExecution:ComposeFiles:1"] = Second,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHostUpdateRecoveryEngine(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<HostUpdateExecutionOptions>>().Value.ComposeFiles
            .Should().Equal(First, Second);
    }

    private static HostUpdateExecutionOptions BindFrom(Dictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new HostUpdateExecutionOptions();
        HostUpdateExecutionOptions.Bind(configuration.GetSection(HostUpdateExecutionOptions.SectionName), options);
        return options;
    }
}
