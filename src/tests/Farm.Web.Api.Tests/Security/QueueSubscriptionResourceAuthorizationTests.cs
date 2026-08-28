using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.SignalR;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class QueueSubscriptionResourceAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new(
        new Dictionary<string, string?>
        {
            ["Testing:UseTestAuthentication"] = "true",
            ["Security:DevModeBypassAuth"] = "false",
        });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task SubscriptionResources_ReturnAllCurrentAuthorizedJobsOnly()
    {
        Guid actorId = Guid.NewGuid();
        Guid printerId = await SeedJobsAsync(actorId);
        using HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", actorId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", "operator");
        client.DefaultRequestHeaders.Add(
            "X-Test-Permissions",
            PrintFarmerPermissions.Queue.Read);

        HttpResponseMessage response = await client.GetAsync(
            "/api/job-queue/subscription-resources");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        QueueSubscriptionResourcesDto? resources =
            await response.Content.ReadFromJsonAsync<QueueSubscriptionResourcesDto>();
        resources.Should().NotBeNull();
        resources!.JobIds.Should().HaveCount(125);
        resources.JobIds.Should().OnlyHaveUniqueItems();
        resources.PrinterIds.Should().Equal(printerId);
    }

    private async Task<Guid> SeedJobsAsync(Guid actorId)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = $"Resource maker {Guid.NewGuid():N}",
        };
        var model = new PrinterModel
        {
            Id = Guid.NewGuid(),
            ManufacturerId = manufacturer.Id,
            Name = $"Resource model {Guid.NewGuid():N}",
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Resource printer",
            ServerUrl = $"http://resource-{Guid.NewGuid():N}",
            ManufacturerId = manufacturer.Id,
            ModelId = model.Id,
            IsEnabled = true,
            IsAvailable = true,
        };
        db.AddRange(manufacturer, model, printer);
        for (int index = 0; index < 125; index++)
        {
            db.PrintJobs.Add(new PrintJob
            {
                Id = Guid.NewGuid(),
                Name = $"Authorized job {index}",
                AssignedPrinterId = printer.Id,
                CreatorSubject = actorId.ToString(),
                Status = PrintJobStatus.Queued,
                Priority = (int)PrintJobPriority.Normal,
                QueuePosition = index + 1,
                CreatedAt = now,
                UpdatedAt = now,
                QueuedAt = now,
            });
        }

        db.PrintJobs.Add(new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Foreign job",
            AssignedPrinterId = printer.Id,
            CreatorSubject = Guid.NewGuid().ToString(),
            Status = PrintJobStatus.Queued,
            Priority = (int)PrintJobPriority.Normal,
            QueuePosition = 126,
            CreatedAt = now,
            UpdatedAt = now,
            QueuedAt = now,
        });
        await db.SaveChangesAsync();
        return printer.Id;
    }
}
