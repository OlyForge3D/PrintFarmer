using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Security;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Farm.Web.Api.Tests.Security;

public sealed class PrinterControlRecoveryAuthorizationTests : IAsyncLifetime, IDisposable
{
    private readonly RecoveryFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public void Dispose() => _factory.Dispose();

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task RemovedRecoveryRoutes_AnyActor_CannotAttestOrMutateHistoricalReceipt(
        bool complete, bool anonymous, bool admin)
    {
        Seed seed = await SeedAsync(complete, PrinterGroupAccessLevel.Submit);
        using HttpClient client = CreateClient(seed, anonymous: anonymous, reconcile: true, admin: admin);
        using HttpRequestMessage request = CreateRequest(seed, complete, ETag(seed.Operation.Revision));

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound,
            await response.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(seed);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "\"AAAAAAAAAAA=\"")]
    [InlineData(true, "\"AAAAAAAAAAA=\"")]
    public async Task RemovedRecoveryRoutes_MissingOrStaleRevision_RemainAbsent(bool complete, string? revision)
    {
        Seed seed = await SeedAsync(complete, PrinterGroupAccessLevel.Submit);
        using HttpClient client = CreateClient(seed, reconcile: true);
        using HttpRequestMessage request = CreateRequest(seed, complete, revision);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(seed);
    }

    [Theory]
    [InlineData(false, false, PrinterGroupAccessLevel.View, HttpStatusCode.OK)]
    [InlineData(false, false, null, HttpStatusCode.NotFound)]
    [InlineData(true, false, PrinterGroupAccessLevel.View, HttpStatusCode.Unauthorized)]
    [InlineData(false, true, null, HttpStatusCode.OK)]
    public async Task StatusRoute_HistoricalUnknown_RequiresReadAccessButNeverAttestation(
        bool anonymous, bool admin, PrinterGroupAccessLevel? access, HttpStatusCode expected)
    {
        Seed seed = await SeedAsync(false, access);
        using HttpClient client = CreateClient(seed, anonymous: anonymous, admin: admin);
        using HttpResponseMessage response = await client.GetAsync(
            $"/api/printers/{seed.Operation.PrinterId}/control-operations/{seed.Operation.Id}");
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        if (expected == HttpStatusCode.OK)
        {
            using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("state").GetString().Should().Be("Unknown");
            body.RootElement.GetProperty("requiresRecovery").GetBoolean().Should().BeFalse();
            body.RootElement.GetProperty("completionEvidence").GetString().Should().Be("None");
            response.Headers.ETag.Should().NotBeNull();
        }
        await AssertUnchangedAsync(seed);
    }

    private async Task AssertUnchangedAsync(Seed seed)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await db.PrinterControlOperations.SingleAsync(o => o.Id == seed.Operation.Id)).Should().BeEquivalentTo(seed.Operation);
        (await db.PrinterDispatchStates.SingleAsync(b => b.PrinterId == seed.Operation.PrinterId)).Should()
            .BeEquivalentTo(seed.Barrier, options => options.Excluding(b => b.Printer));
        (await db.QueueOperationAudits.AnyAsync(a => a.ResourceId == seed.Operation.Id)).Should().BeFalse();
        (await db.QueueDispatchOutbox.AnyAsync(e => e.AggregateId == seed.Operation.Id)).Should().BeFalse();
    }

    private HttpClient CreateClient(Seed seed, bool anonymous = false, bool reconcile = false, bool admin = false)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", seed.UserId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", admin ? "farm_admin" : "recovery-operator");
        if (anonymous) client.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        if (reconcile) client.DefaultRequestHeaders.Add("X-Test-Permissions", PrintFarmerPermissions.Queue.Reconcile);
        return client;
    }

    private static string ETag(long revision) => $"\"{Convert.ToBase64String(RevisionETag.EncodeBytes(revision))}\"";

    private static HttpRequestMessage CreateRequest(Seed seed, bool complete, string? revision)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/printers/{seed.Operation.PrinterId}/control-operations/{seed.Operation.Id}/recovery{(complete ? "/complete" : "")}");
        if (revision is not null) request.Headers.TryAddWithoutValidation("If-Match", revision);
        if (complete)
        {
            request.Content = JsonContent.Create(new
            {
                reason = "fixture recovery",
                senderIsolation = "ExternallyVerified",
                senderIsolationEvidence = "fixture sender isolated",
                controllerQueueCleared = true,
                physicallyStationary = true,
                physicalEvidence = "fixture inspection",
            });
        }

        return request;
    }

    private async Task<Seed> SeedAsync(bool complete, PrinterGroupAccessLevel? access)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Recovery maker {Guid.NewGuid():N}" };
        var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Recovery model" };
        var group = new PrinterGroup { Id = Guid.NewGuid(), Name = $"Recovery group {Guid.NewGuid():N}" };
        var role = new Role
        {
            Id = Guid.NewGuid(), Name = $"recovery-{Guid.NewGuid():N}", DisplayName = "Recovery operator",
            IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        var user = new User
        {
            Id = Guid.NewGuid(), Username = $"recovery-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@example.invalid",
            PasswordHash = "unused", FirstName = "Recovery", LastName = "Operator", IsActive = true,
            EmailConfirmed = true, CreatedAt = now, UpdatedAt = now,
        };
        var printer = new Printer
        {
            Id = Guid.NewGuid(), Name = "Recovery fixture", ServerUrl = "http://recovery.invalid",
            ManufacturerId = manufacturer.Id, ModelId = model.Id, PrinterGroupId = group.Id,
            Backend = (int)PrinterBackend.Moonraker, IsEnabled = true, IsAvailable = true,
        };
        var operation = new PrinterControlOperation
        {
            Id = Guid.NewGuid(), PrinterId = printer.Id, Kind = PrinterControlKind.HomeAll,
            State = complete ? PrinterControlState.Recovering : PrinterControlState.Unknown,
            SenderIsolation = PrinterSenderIsolation.ExternalVerificationRequired,
            ActorSubject = user.Id.ToString(), CreatedAtUtc = now, UpdatedAtUtc = now,
        };
        var barrier = new PrinterDispatchState
        {
            PrinterId = printer.Id, PhysicalControlCommandId = operation.Id,
            PhysicalControlOperation = "HomeAll", PhysicalControlRequiresReconciliation = true,
        };
        db.AddRange(manufacturer, model, group, role, user, printer, operation, barrier,
            new PrinterGroupAccess
            {
                Id = Guid.NewGuid(), PrinterGroupId = group.Id, RoleId = role.Id,
                AccessLevel = access ?? PrinterGroupAccessLevel.Submit,
            });
        if (access.HasValue)
        {
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(), UserId = user.Id, RoleId = role.Id, IsActive = true, AssignedAt = now,
            });
        }

        await db.SaveChangesAsync();
        return new Seed(user.Id, operation, barrier);
    }

    private sealed record Seed(Guid UserId, PrinterControlOperation Operation, PrinterDispatchState Barrier);

    private sealed class RecoveryFactory() : CustomWebApplicationFactory(new Dictionary<string, string?>
    {
        ["Testing:UseTestAuthentication"] = "true",
        ["Security:DevModeBypassAuth"] = "false",
    })
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // Recovery HTTP tests own seeded state; no background motion execution is allowed.
                foreach (ServiceDescriptor descriptor in services.Where(d =>
                    d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(PrinterControlOperationWorker)).ToArray())
                {
                    services.Remove(descriptor);
                }
            });
        }
    }
}
