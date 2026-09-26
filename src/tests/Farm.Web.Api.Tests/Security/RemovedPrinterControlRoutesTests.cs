using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Security;

public sealed class RemovedPrinterControlRoutesTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory factory = new(new Dictionary<string, string?>
    {
        ["Testing:UseTestAuthentication"] = "true",
        ["Security:DevModeBypassAuth"] = "false",
    });

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await factory.DisposeAsync();

    public void Dispose() => factory.Dispose();

    [Theory]
    [InlineData("POST", "", false)]
    [InlineData("GET", "/current", false)]
    [InlineData("GET", "/operation", false)]
    [InlineData("POST", "/operation/recovery", false)]
    [InlineData("POST", "/operation/recovery/complete", false)]
    [InlineData("POST", "", true)]
    [InlineData("GET", "/current", true)]
    [InlineData("GET", "/operation", true)]
    [InlineData("POST", "/operation/recovery", true)]
    [InlineData("POST", "/operation/recovery/complete", true)]
    public async Task ControlOperationRoutes_AuthenticatedActor_Return404WithoutEffects(
        string method, string suffix, bool admin)
    {
        Guid userId = Guid.NewGuid();
        Guid printerId = Guid.NewGuid();
        Guid operationId = Guid.NewGuid();
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = $"Direct maker {Guid.NewGuid():N}" };
            var model = new PrinterModel { Id = Guid.NewGuid(), ManufacturerId = manufacturer.Id, Name = "Direct model" };
            db.AddRange(manufacturer, model);
            db.Users.Add(new User
            {
                Id = userId,
                Username = $"direct-{userId:N}",
                Email = $"{userId:N}@example.invalid",
                PasswordHash = "unused",
                FirstName = "Direct",
                LastName = "Operator",
                IsActive = true,
                EmailConfirmed = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });
            db.Printers.Add(new Printer
            {
                Id = printerId,
                Name = "Removed route fixture",
                Backend = (int)PrinterBackend.Moonraker,
                ServerUrl = "http://removed-route.invalid",
                IsEnabled = true,
                IsAvailable = true,
                ManufacturerId = manufacturer.Id,
                ModelId = model.Id,
            });
            db.PrinterDispatchStates.Add(new PrinterDispatchState { PrinterId = printerId });
            await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Id", userId.ToString());
        client.DefaultRequestHeaders.Add("X-Test-Roles", admin ? "farm_admin" : "operator");
        client.DefaultRequestHeaders.Add("X-Test-Permissions", "queue:start,queue:reconcile");
        string path = suffix.Replace("operation", operationId.ToString(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/printers/{printerId}/control-operations{path}");
        request.Headers.TryAddWithoutValidation("If-Match", "\"stale-revision\"");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", operationId.ToString());
        if (method == "POST")
        {
            request.Content = JsonContent.Create(new { kind = "HomeAll", reason = "legacy caller" });
        }

        using HttpResponseMessage response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        await using AsyncServiceScope verifyScope = factory.Services.CreateAsyncScope();
        AppDbContext verify = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        PrinterDispatchState state = await verify.PrinterDispatchStates.SingleAsync(value => value.PrinterId == printerId);
        state.PhysicalControlCommandId.Should().BeNull();
        state.ActiveDispatchAttemptId.Should().BeNull();
        state.PhysicalControlRequiresReconciliation.Should().BeFalse();
        (await verify.QueueOperationAudits.AnyAsync(value => value.PrinterId == printerId)).Should().BeFalse();
        (await verify.QueueDispatchOutbox.AnyAsync(value => value.PrinterId == printerId)).Should().BeFalse();
    }
}
