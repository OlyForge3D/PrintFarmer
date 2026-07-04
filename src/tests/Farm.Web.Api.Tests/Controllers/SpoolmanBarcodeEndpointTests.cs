using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Authentication;
using Farm.Infrastructure.Services.Interfaces;
using Farm.Infrastructure.Settings;
using Farm.Web.Api.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Farm.Web.Api.Tests.Controllers;

public class SpoolmanBarcodeEndpointTests
{
    private readonly Mock<ISpoolmanService> spoolmanServiceMock = new();
    private readonly SpoolmanController controller;

    public SpoolmanBarcodeEndpointTests()
    {
        Mock<ISettingsService> settingsServiceMock = new();
        Mock<ILogger<SpoolmanController>> loggerMock = new();
        controller = new SpoolmanController(spoolmanServiceMock.Object, settingsServiceMock.Object, loggerMock.Object);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_KnownArticleNumber_ReturnsOkWithFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(42, "012345678905");
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("012345678905", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("012345678905", CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(42, value.Id);
        Assert.Equal("012345678905", value.ArticleNumber);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_UnknownArticleNumber_ReturnsNotFound()
    {
        spoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("missing", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_EmptyCode_ReturnsBadRequest()
    {
        ActionResult<SpoolmanFilamentDto> result = await controller.GetFilamentByBarcodeAsync("   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetFilamentByBarcodeAsync_QueryCodeWithSlashPercentAndSpace_ReturnsOkWithFilament()
    {
        const string barcode = "ABC/DEF 12%3";
        SpoolmanFilamentDto filament = CreateFilament(44, barcode);
        Mock<ISpoolmanService> routedSpoolmanServiceMock = new();
        routedSpoolmanServiceMock
            .Setup(s => s.GetFilamentByBarcodeAsync(barcode, It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        await using WebApplicationFactory<Program> factory = CustomWebApplicationFactory
            .CreateWithIsolatedDatabase()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<ISpoolmanService>();
                    services.AddSingleton(routedSpoolmanServiceMock.Object);
                });
            });
        using HttpClient client = await CreateAuthenticatedClientAsync(factory);

        HttpResponseMessage response = await client.GetAsync($"/api/spoolman/filaments/by-barcode?code={Uri.EscapeDataString(barcode)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        SpoolmanFilamentDto? value = await response.Content.ReadFromJsonAsync<SpoolmanFilamentDto>();
        Assert.NotNull(value);
        Assert.Equal(44, value.Id);
        Assert.Equal(barcode, value.ArticleNumber);
        routedSpoolmanServiceMock.Verify(
            s => s.GetFilamentByBarcodeAsync(barcode, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_ValidRequest_ReturnsUpdatedFilament()
    {
        SpoolmanFilamentDto filament = CreateFilament(7, "ABC123");
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 7 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(7, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(filament);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result.Result);
        SpoolmanFilamentDto value = Assert.IsType<SpoolmanFilamentDto>(ok.Value);
        Assert.Equal(7, value.Id);
        Assert.Equal("ABC123", value.ArticleNumber);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_MissingFilament_ReturnsNotFound()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = "ABC123", FilamentId = 404 };
        spoolmanServiceMock
            .Setup(s => s.SaveBarcodeMappingAsync(404, "ABC123", It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanFilamentDto?)null);

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task SaveBarcodeMappingAsync_EmptyBarcode_ReturnsBadRequest()
    {
        SpoolmanBarcodeMappingRequest request = new() { Barcode = " ", FilamentId = 7 };

        ActionResult<SpoolmanFilamentDto> result = await controller.SaveBarcodeMappingAsync(request, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_KnownBarcode_ReturnsCreatedSpool()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new()
        {
            Barcode = "ABC123",
            RemainingWeight = 950,
            Location = "Shelf A",
        };
        SpoolmanSpoolDto spool = new(99, "PLA", "PLA", 950, null, false, FilamentId: 7, Location: "Shelf A");
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync(spool);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        ObjectResult created = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(201, created.StatusCode);
        SpoolmanSpoolDto value = Assert.IsType<SpoolmanSpoolDto>(created.Value);
        Assert.Equal(7, value.FilamentId);
        Assert.Equal(950, value.RemainingWeightG);
        Assert.Equal("Shelf A", value.Location);
    }

    [Fact]
    public async Task CreateSpoolByBarcodeAsync_UnknownBarcode_ReturnsNotFound()
    {
        SpoolmanImportSpoolByBarcodeRequest request = new() { Barcode = "missing" };
        spoolmanServiceMock
            .Setup(s => s.CreateSpoolByBarcodeAsync(request, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SpoolmanSpoolDto?)null);

        ActionResult<SpoolmanSpoolDto> result = await controller.CreateSpoolByBarcodeAsync(request, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    private static SpoolmanFilamentDto CreateFilament(int id, string? articleNumber)
        => new(
            Id: id,
            Name: "PolyTerra PLA",
            Material: "PLA",
            ColorHex: "111111",
            Vendor: "Polymaker",
            Density: 1.24,
            Diameter: 1.75,
            Weight: 1000,
            SpoolWeight: 200,
            Price: 24.99,
            SettingsExtruderTemp: 210,
            SettingsBedTemp: 60,
            ArticleNumber: articleNumber,
            Comment: null,
            MultiColorHexes: null,
            ExternalId: null);

    private static async Task<HttpClient> CreateAuthenticatedClientAsync(
        WebApplicationFactory<Program> factory,
        string username = "test-admin",
        string email = "test@example.com",
        string password = "TestPassword123!")
    {
        using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            IPasswordHashingService passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHashingService>();

            User? existingUser = context.Users.FirstOrDefault(u => u.Username == username);
            if (existingUser is null)
            {
                context.Users.Add(new User
                {
                    Id = Guid.NewGuid(),
                    Username = username,
                    Email = email,
                    PasswordHash = passwordHasher.HashPassword(password),
                    FirstName = "Test",
                    LastName = "Admin",
                    IsActive = true,
                    EmailConfirmed = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                });
                await context.SaveChangesAsync();
            }
        }

        using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            IAuthenticationService authService = scope.ServiceProvider.GetRequiredService<IAuthenticationService>();
            AuthenticationResult result = await authService.AuthenticateAsync(username, password);
            Assert.True(result.Success);
            Assert.False(string.IsNullOrEmpty(result.Token));

            HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", result.Token);
            return client;
        }
    }
}
