using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

[Trait("Category", "Integration")]
public sealed class PrinterMonitoringControllerTests : IClassFixture<PrinterMonitoringControllerTests.MonitoringControllerFactory>
{
    private readonly MonitoringControllerFactory _factory;

    public PrinterMonitoringControllerTests(MonitoringControllerFactory factory) => _factory = factory;

    public sealed class MonitoringControllerFactory : CustomWebApplicationFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Controller preconditions are exercised deterministically; reconciliation has its own tests.
                ServiceDescriptor? reconciler = services.SingleOrDefault(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(PrinterMonitoringReconnectService));
                if (reconciler is not null)
                {
                    services.Remove(reconciler);
                }
            });
        }
    }

    [Fact]
    public async Task Intent_PersistsAcrossRequestsAndCanBeReversedWithoutChangingDispatch()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid id = await SeedAsync();
        Cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        HttpResponseMessage response = await PutAsync(client, id, true);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PrinterMonitoringDto? result = await response.Content.ReadFromJsonAsync<PrinterMonitoringDto>();
        result!.ExpectedOfflineSinceUtc.Should().NotBeNull();
        result.RowVersion.Should().NotBeNullOrEmpty();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            Printer printer = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Printers.SingleAsync(value => value.Id == id);
            printer.ExpectedOfflineSinceUtc.Should().NotBeNull();
            printer.IsEnabled.Should().BeTrue();
            printer.InMaintenance.Should().BeFalse();
        }

        (await PutAsync(client, id, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<AppDbContext>().Printers.SingleAsync(value => value.Id == id))
            .ExpectedOfflineSinceUtc.Should().BeNull();
    }

    [Theory]
    [InlineData(true, "Idle")]
    [InlineData(false, "Unknown")]
    [InlineData(false, "Error")]
    [InlineData(false, "Printing")]
    public async Task UnsafeStatus_CannotBeAcknowledged(bool online, string state)
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid id = await SeedAsync();
        Cache.UpdateStatus(new PrinterStatusDto(id, online, state));
        (await PutAsync(client, id, true)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ReacknowledgingAfterReconnect_UsesNewIntentInsteadOfExpiredAcknowledgement()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid id = await SeedAsync();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Printer printer = await db.Printers.SingleAsync(value => value.Id == id);
            printer.ExpectedOfflineSinceUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        }

        DateTime observed = DateTime.UtcNow.AddMinutes(-1);
        Cache.UpdateStatus(new PrinterStatusDto(id, true, "Idle") { ObservationStartedAtUtc = observed });
        Cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        HttpResponseMessage response = await PutAsync(client, id, true);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PrinterMonitoringDto? result = await response.Content.ReadFromJsonAsync<PrinterMonitoringDto>();
        result!.ExpectedOfflineSinceUtc.Should().BeAfter(observed);
    }

    [Fact]
    public async Task AssignedJob_CannotBeAcknowledged()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid id = await SeedAsync();
        Cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PrintJobs.Add(new PrintJob { Name = "Synthetic active work", AssignedPrinterId = id, Status = PrintJobStatus.Assigned });
            await db.SaveChangesAsync();
        }

        (await PutAsync(client, id, true)).StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Mutation_RequiresPermissionAndReviewedRevision()
    {
        using HttpClient admin = await _factory.CreateAdminClientAsync();
        Guid id = await SeedAsync();
        Cache.UpdateStatus(new PrinterStatusDto(id, false, "Offline"));
        using HttpClient user = await _factory.CreateAuthenticatedClientAsync("monitoring-no-permissions", "monitoring@test.invalid");
        (await user.PutAsJsonAsync($"/api/printers/{id}/expected-offline", true)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await admin.PutAsJsonAsync($"/api/printers/{id}/expected-offline", true)).StatusCode.Should().Be((HttpStatusCode)428);
        using HttpRequestMessage stale = new(HttpMethod.Put, $"/api/printers/{id}/expected-offline")
        {
            Content = JsonContent.Create(true),
        };
        stale.Headers.IfMatch.ParseAdd($"\"{Convert.ToBase64String(new byte[8])}\"");
        (await admin.SendAsync(stale)).StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Printers.SingleAsync(value => value.Id == id))
            .ExpectedOfflineSinceUtc.Should().BeNull();
    }

    private IPrinterStatusCacheWriter Cache => _factory.Services.GetRequiredService<IPrinterStatusCacheWriter>();

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, Guid id, bool expectedOffline)
    {
        using HttpResponseMessage current = await client.GetAsync($"/api/printers/{id}");
        current.EnsureSuccessStatusCode();
        using HttpRequestMessage request = new(HttpMethod.Put, $"/api/printers/{id}/expected-offline")
        {
            Content = JsonContent.Create(expectedOffline),
        };
        request.Headers.IfMatch.ParseAdd(current.Headers.ETag!.Tag);
        return await client.SendAsync(request);
    }

    private async Task<Guid> SeedAsync()
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Manufacturer manufacturer = new() { Name = $"Synthetic {Guid.NewGuid():N}" };
        PrinterModel model = new() { Name = "Synthetic", Manufacturer = manufacturer };
        Printer printer = new()
        {
            Name = "Synthetic monitoring printer",
            Manufacturer = manufacturer,
            Model = model,
            ServerUrl = $"http://synthetic-{Guid.NewGuid():N}.invalid:1",
            Backend = (int)PrinterBackend.PrusaLink,
            IsEnabled = true,
        };
        db.Printers.Add(printer);
        await db.SaveChangesAsync();
        return printer.Id;
    }
}
