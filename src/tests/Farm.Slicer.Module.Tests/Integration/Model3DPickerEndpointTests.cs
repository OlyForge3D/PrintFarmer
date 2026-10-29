using System.Net;
using System.Text.Json;
using Farm.Slicer.Module.Domain;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Farm.Slicer.Module.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class Model3DPickerEndpointTests : IAsyncLifetime, IDisposable
{
    private readonly CustomWebApplicationFactory _factory = new();
    private HttpClient? _authenticatedClient;

    public async Task InitializeAsync()
    {
        await _factory.ResetDatabaseAsync();
        _authenticatedClient = await _factory.CreateAuthenticatedClientAsync();
    }

    public Task DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _authenticatedClient?.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task GetPickerModels_ReturnsOnlyValidSharedModelsWithPickerFieldsAndOrder()
    {
        DateTime olderDate = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Model3D olderModel = CreateModel("older-display.stl", "older-storage.stl", olderDate, Guid.NewGuid());
        Model3D newerModel = CreateModel("newer-display.3mf", "newer-storage.3mf", olderDate.AddHours(1), Guid.NewGuid(), ModelFileFormat.TMF);
        Model3D invalidModel = CreateModel("invalid.stl", "invalid-storage.stl", olderDate.AddHours(2), Guid.NewGuid(), isValid: false);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            SlicerDbContext db = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            db.Models3D.AddRange(olderModel, newerModel, invalidModel);
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage response = await _authenticatedClient!.GetAsync("/api/3d-models/picker");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] models = document.RootElement.EnumerateArray().ToArray();
        models.Should().HaveCount(2);
        models[0].GetProperty("id").GetGuid().Should().Be(newerModel.Id);
        models[0].GetProperty("name").GetString().Should().Be("newer-display.3mf");
        models[0].GetProperty("fileName").GetString().Should().Be("newer-storage.3mf");
        models[0].GetProperty("fileType").GetString().Should().Be("3mf");
        models[0].GetProperty("uploadedAt").GetDateTime().Should().Be(newerModel.UploadedAt);
        models[1].GetProperty("id").GetGuid().Should().Be(olderModel.Id);
        models[1].GetProperty("fileType").GetString().Should().Be("stl");
        models.SelectMany(model => model.EnumerateObject().Select(property => property.Name))
            .Distinct()
            .Order(StringComparer.Ordinal)
            .Should()
            .Equal("fileName", "fileType", "id", "name", "uploadedAt");
        models.Select(model => model.GetProperty("id").GetGuid()).Should().NotContain(invalidModel.Id);
    }

    [Fact]
    public async Task GetPickerModels_WithoutAuthentication_ReturnsUnauthorized()
    {
        using HttpClient anonymousClient = _factory.CreateClient();

        using HttpResponseMessage response = await anonymousClient.GetAsync("/api/3d-models/picker");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static Model3D CreateModel(
        string name,
        string fileName,
        DateTime uploadedAt,
        Guid uploaderId,
        ModelFileFormat fileFormat = ModelFileFormat.STL,
        bool isValid = true)
    {
        Guid id = Guid.NewGuid();
        return new Model3D
        {
            Id = id,
            Name = name,
            FileName = fileName,
            FilePath = "/models",
            FileSizeBytes = 1,
            FileHash = id.ToString("N"),
            FileFormat = fileFormat,
            UploadedAt = uploadedAt,
            UploadedByUserId = uploaderId,
            IsValid = isValid,
            ExtractedMetadataJson = "{\"metadata\":\"not returned\"}",
            SourceUrl = "https://example.test/model",
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt,
        };
    }
}
