using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
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
            ["Slicer:Enabled"] = "false"
        });
        using HttpClient client = await factory.CreateAdminClientAsync();

        HttpResponseMessage health = await client.GetAsync("/healthz");
        HttpResponseMessage policy = await client.GetAsync("/api/admin/host-updates/automation-policy");
        HttpResponseMessage status = await client.GetAsync("/api/admin/host-updates/release-default/status");
        HttpResponseMessage execute = await client.PostAsJsonAsync("/api/admin/host-updates/execute", new { });

        health.StatusCode.Should().Be(HttpStatusCode.OK);
        policy.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        status.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        execute.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await policy.Content.ReadAsStringAsync()).Should().Contain("host_update_policy_repository_not_available");
        (await status.Content.ReadAsStringAsync()).Should().Contain("host_update_execution_journal_not_available");
        (await execute.Content.ReadAsStringAsync()).Should().Contain("host_update_manual_authorization_not_available");
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
}
