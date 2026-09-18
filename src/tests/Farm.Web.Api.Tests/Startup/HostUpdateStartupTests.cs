using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Services.HostUpdates;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Farm.Web.Api.Tests.Startup;

public sealed class HostUpdateStartupTests
{
    [Fact]
    public async Task DefaultUnprovisionedHost_StartsAndHostUpdateAdminRoutesReturnAvailability503()
    {
        await using CustomWebApplicationFactory factory = new(new Dictionary<string, string?>
        {
            ["Slicer:Enabled"] = "false",
            ["HostUpdateExecution:RootDirectory"] = string.Empty,
        });
        using HttpClient client = await factory.CreateAdminClientAsync();
        HostUpdateExecutionAvailabilityHolder availability = factory.Services.GetRequiredService<HostUpdateExecutionAvailabilityHolder>();

        HttpResponseMessage health = await client.GetAsync("/healthz");
        HttpResponseMessage policy = await client.GetAsync("/api/admin/host-updates/automation-policy");
        HttpResponseMessage status = await client.GetAsync("/api/admin/host-updates/release-default/status");
        HttpResponseMessage execute = await client.PostAsJsonAsync("/api/admin/host-updates/execute", new { });
        await WaitForAvailabilityCheckAsync(availability);

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        policy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        status.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        execute.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await policy.Content.ReadAsStringAsync()).Should().Contain("host_update_policy_repository_not_available");
        (await status.Content.ReadAsStringAsync()).Should().Contain("host_update_execution_journal_not_available");
        (await execute.Content.ReadAsStringAsync()).Should().Contain("host_update_manual_authorization_not_available");
        availability.Current.State.Should().Be(HostUpdateExecutionAvailabilityState.Unavailable);
        availability.Current.Reasons.Should().Contain("root_directory_not_configured");
        availability.Current.Reasons.Should().Contain("host_update_execution_journal_not_available");
    }

    [Fact]
    public void EnabledHostStateWithInvalidRoot_FailsStartupValidation()
    {
        using CustomWebApplicationFactory factory = new(new Dictionary<string, string?>
        {
            ["Slicer:Enabled"] = "false",
            ["HostUpdates:HostState:Enabled"] = "true",
            ["HostUpdates:HostState:RootPath"] = "relative-host-state"
        });

        Action act = () => factory.CreateClient();

        act.Should().Throw<OptionsValidationException>()
            .WithMessage("*HostUpdates:HostState:RootPath must be an absolute persistent path.*");
    }

    private static async Task WaitForAvailabilityCheckAsync(HostUpdateExecutionAvailabilityHolder availability)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (availability.Current.Reasons.Contains("not_yet_checked"))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
        }
    }
}
