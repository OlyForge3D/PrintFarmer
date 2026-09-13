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
    [InlineData(false, true, true, PrinterGroupAccessLevel.Submit, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, true, PrinterGroupAccessLevel.Submit, HttpStatusCode.Unauthorized)]
    [InlineData(false, false, false, PrinterGroupAccessLevel.Submit, HttpStatusCode.Forbidden)]
    [InlineData(true, false, false, PrinterGroupAccessLevel.Submit, HttpStatusCode.Forbidden)]
    [InlineData(false, false, true, PrinterGroupAccessLevel.View, HttpStatusCode.NotFound)]
    [InlineData(true, false, true, PrinterGroupAccessLevel.View, HttpStatusCode.NotFound)]
    [InlineData(false, false, true, null, HttpStatusCode.NotFound)]
    [InlineData(true, false, true, null, HttpStatusCode.NotFound)]
    public async Task Recovery_WithoutRequiredGrants_PreservesOperationAndBarrier(
        bool complete, bool anonymous, bool reconcile, PrinterGroupAccessLevel? access, HttpStatusCode expected)
    {
        Seed seed = await SeedAsync(complete, access);
        using HttpClient client = CreateClient(seed, anonymous: anonymous, reconcile: reconcile);
        using HttpRequestMessage request = CreateRequest(seed, complete, ETag(seed.Operation.Revision));

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(seed);
    }

    [Theory]
    [InlineData(false, null, (HttpStatusCode)428)]
    [InlineData(true, null, (HttpStatusCode)428)]
    [InlineData(false, "\"AAAAAAAAAAA=\"", HttpStatusCode.PreconditionFailed)]
    [InlineData(true, "\"AAAAAAAAAAA=\"", HttpStatusCode.PreconditionFailed)]
    public async Task Recovery_GrantedOperatorStillRequiresCurrentRevision(
        bool complete, string? revision, HttpStatusCode expected)
    {
        Seed seed = await SeedAsync(complete, PrinterGroupAccessLevel.Submit);
        using HttpClient client = CreateClient(seed, reconcile: true);
        using HttpRequestMessage request = CreateRequest(seed, complete, revision);

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync());
        await AssertUnchangedAsync(seed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_GrantedOperatorOrAdministrator_RecordsRecoveryNotMotionSuccess(bool admin)
    {
        // The administrator deliberately has neither a reconcile claim nor a printer-group grant.
        Seed seed = await SeedAsync(false, admin ? null : PrinterGroupAccessLevel.Submit);
        using HttpClient client = CreateClient(seed, admin: admin, reconcile: !admin);
        using HttpRequestMessage begin = CreateRequest(seed, false, ETag(seed.Operation.Revision));
        using HttpResponseMessage begun = await client.SendAsync(begin);
        begun.StatusCode.Should().Be(HttpStatusCode.Accepted, await begun.Content.ReadAsStringAsync());
        begun.Headers.ETag.Should().NotBeNull();
        using (JsonDocument body = JsonDocument.Parse(await begun.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("state").GetString().Should().Be("Recovering");
        }

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            PrinterDispatchState barrier = await db.PrinterDispatchStates.SingleAsync(b => b.PrinterId == seed.Operation.PrinterId);
            barrier.PhysicalControlCommandId.Should().Be(seed.Operation.Id);
            barrier.PhysicalControlRequiresReconciliation.Should().BeTrue();
        }

        using HttpRequestMessage complete = CreateRequest(seed, true, begun.Headers.ETag!.ToString());
        using HttpResponseMessage completed = await client.SendAsync(complete);
        completed.StatusCode.Should().Be(HttpStatusCode.OK, await completed.Content.ReadAsStringAsync());
        using (JsonDocument body = JsonDocument.Parse(await completed.Content.ReadAsStringAsync()))
        {
            body.RootElement.GetProperty("state").GetString().Should().Be("Recovered");
        }

        await using AsyncServiceScope finalScope = _factory.Services.CreateAsyncScope();
        AppDbContext finalDb = finalScope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterControlOperation operation = await finalDb.PrinterControlOperations.SingleAsync(o => o.Id == seed.Operation.Id);
        operation.State.Should().Be(PrinterControlState.Recovered);
        operation.CompletionEvidence.Should().Be(PrinterControlEvidence.OperatorVerifiedRecovery);
        operation.RecoveryActorSubject.Should().Be(seed.UserId.ToString());
        operation.RecoveryFromRevision.Should().Be(seed.Operation.Revision + 1);
        operation.Revision.Should().Be(seed.Operation.Revision + 2);
        operation.RecoveryEvidenceJson.Should().Contain("ExternallyVerified").And.Contain("fixture inspection");
        operation.SendCommittedAtUtc.Should().BeNull();
        operation.OwnerToken.Should().BeNull();
        PrinterDispatchState finalBarrier = await finalDb.PrinterDispatchStates.SingleAsync(b => b.PrinterId == operation.PrinterId);
        finalBarrier.PhysicalControlCommandId.Should().BeNull();
        finalBarrier.PhysicalControlRequiresReconciliation.Should().BeFalse();
        List<QueueOperationAudit> audits = await finalDb.QueueOperationAudits.Where(a => a.ResourceId == operation.Id).ToListAsync();
        audits.Should().HaveCount(2).And.OnlyContain(a => a.ActorSubject == seed.UserId.ToString());
        (await finalDb.QueueDispatchOutbox.CountAsync(e => e.AggregateId == operation.Id)).Should().Be(2);
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
