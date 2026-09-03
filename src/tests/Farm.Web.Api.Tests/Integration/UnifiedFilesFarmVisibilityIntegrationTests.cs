using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Dtos;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests.Integration;

/// <summary>
/// Documents that the authenticated file library is farm-global even when a durable G-code file
/// originated from an owner-scoped temporary slice artifact.
/// </summary>
public sealed class UnifiedFilesFarmVisibilityIntegrationTests : IAsyncLifetime
{
    private readonly CustomWebApplicationFactory _factory =
        CustomWebApplicationFactory.CreateWithIsolatedDatabase();

    public async Task InitializeAsync()
    {
        await _factory.ResetDataAsync();
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task QueryAsync_PromotedFileRequestedByNonOwner_ReturnsFarmGlobalGcode()
    {
        Guid ownerUserId = Guid.NewGuid();
        Guid sourceJobId = Guid.NewGuid();
        Guid sourceArtifactId = Guid.NewGuid();
        Guid gcodeFileId = Guid.NewGuid();
        DateTime now = DateTime.UtcNow;

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            AppDbContext appDb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            SlicerDbContext slicerDb = scope.ServiceProvider.GetRequiredService<SlicerDbContext>();
            FolderNode root = await appDb.Set<FolderNode>()
                .SingleAsync(folder => folder.Path == "/" && folder.FolderType == "gcode");
            _ = slicerDb.SliceJobs.Add(new SliceJob
            {
                Id = sourceJobId,
                UserId = ownerUserId,
                ModelFileUrl = "/api/3d-models/owner-model",
                ModelFileName = "owner-model.3mf",
                Status = SliceJobStatus.Completed,
                QueuedAt = now.AddMinutes(-1),
                CompletedAt = now,
            });
            _ = appDb.GcodeFiles.Add(new GcodeFile
            {
                Id = gcodeFileId,
                Name = "owner-promoted.gcode",
                FileName = "owner-promoted.gcode",
                FilePath = "/owner-promoted.gcode",
                FolderId = root.Id,
                FileSizeBytes = 321,
                FileHash = new string('A', 64),
                Source = GcodeSource.Generated,
                SourceSliceJobId = sourceJobId,
                SourceArtifactId = sourceArtifactId,
                IsImmutable = true,
                UploadedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
            _ = await slicerDb.SaveChangesAsync();
            _ = await appDb.SaveChangesAsync();
        }

        using HttpClient nonOwner = await _factory.CreateAuthenticatedClientAsync(
            username: "gcode-library-non-owner",
            email: "gcode-library-non-owner@example.com");
        using HttpResponseMessage response = await nonOwner.PostAsJsonAsync(
            "/api/3d-models/files/query",
            new UnifiedFilesQueryRequestDto
            {
                Filter = UnifiedFileTypeFilter.Gcode,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] items = [.. body.RootElement.GetProperty("items").EnumerateArray()];
        items.Should().ContainSingle();
        JsonElement item = items[0];
        item.GetProperty("id").GetGuid().Should().Be(gcodeFileId);
        item.GetProperty("name").GetString().Should().Be("owner-promoted.gcode");
    }
}
