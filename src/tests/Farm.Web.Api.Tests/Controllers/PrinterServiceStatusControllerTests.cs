using System.Net;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Printers;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Controllers;

[Trait("Category", "Integration")]
public sealed class PrinterServiceStatusControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PrinterServiceStatusControllerTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task OutOfServiceTransition_IsRejectedWhenAssignedWorkExists()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid printerId = await SeedAsync();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.PrintJobs.Add(new PrintJob
            {
                Name = "Assigned work",
                AssignedPrinterId = printerId,
                Status = PrintJobStatus.Assigned,
            });
            await db.SaveChangesAsync();
        }

        HttpResponseMessage response = await UpdateServiceStatusAsync(client, printerId, isEnabled: false);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<AppDbContext>().Printers
                .SingleAsync(printer => printer.Id == printerId))
            .IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task OutOfServiceTransition_IsRejectedWhenCachedPrinterStateShowsPrinting()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid printerId = await SeedAsync();
        _factory.Services.GetRequiredService<IPrinterStatusCacheWriter>()
            .UpdateStatus(new PrinterStatusDto(printerId, true, "Printing"));

        HttpResponseMessage response = await UpdateServiceStatusAsync(client, printerId, isEnabled: false);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task OutOfServiceTransition_SucceedsWhenNoWorkExists()
    {
        using HttpClient client = await _factory.CreateAdminClientAsync();
        Guid printerId = await SeedAsync();

        HttpResponseMessage response = await UpdateServiceStatusAsync(client, printerId, isEnabled: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await using AsyncServiceScope verify = _factory.Services.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<AppDbContext>().Printers
                .SingleAsync(printer => printer.Id == printerId))
            .IsEnabled.Should().BeFalse();
    }

    private static async Task<HttpResponseMessage> UpdateServiceStatusAsync(
        HttpClient client,
        Guid printerId,
        bool isEnabled)
    {
        using HttpResponseMessage current = await client.GetAsync($"/api/printers/{printerId}");
        current.EnsureSuccessStatusCode();
        using HttpRequestMessage request = new(HttpMethod.Put, $"/api/printers/{printerId}")
        {
            Content = JsonContent.Create(new UpdatePrinterDto(IsEnabled: isEnabled)),
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
            Name = "Synthetic status printer",
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
