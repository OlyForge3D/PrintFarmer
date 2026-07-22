using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Data;

public class Model3DIdempotencyPersistenceTests
{
    [Fact]
    public async Task SaveChangesAsync_WithDuplicateClientUploadIdForUser_RejectsDuplicate()
    {
        await using SlicerDbContext context = TestHelpers.CreateSqliteInMemoryDb();
        Guid userId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();
        context.Models3D.Add(CreateModel(userId, clientUploadId));
        await context.SaveChangesAsync();
        context.Models3D.Add(CreateModel(userId, clientUploadId));

        _ = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task SaveChangesAsync_WithNullClientUploadIds_AllowsLegacyRows()
    {
        await using SlicerDbContext context = TestHelpers.CreateSqliteInMemoryDb();
        context.Models3D.Add(CreateModel(userId: null, clientUploadId: null));
        context.Models3D.Add(CreateModel(userId: null, clientUploadId: null));

        _ = await context.SaveChangesAsync();

        Assert.Equal(2, await context.Models3D.CountAsync());
    }

    [Fact]
    public async Task GetByClientUploadIdAsync_WithSameIdForDifferentUsers_ReturnsOwnedModel()
    {
        await using SlicerDbContext context = TestHelpers.CreateSqliteInMemoryDb();
        Guid firstUserId = Guid.NewGuid();
        Guid secondUserId = Guid.NewGuid();
        Guid clientUploadId = Guid.NewGuid();
        Model3D firstModel = CreateModel(firstUserId, clientUploadId);
        Model3D secondModel = CreateModel(secondUserId, clientUploadId);
        context.Models3D.AddRange(firstModel, secondModel);
        _ = await context.SaveChangesAsync();
        EfModel3DFileRepository repository = new(context);

        Model3D? result = await repository.GetByClientUploadIdAsync(
            secondUserId,
            clientUploadId,
            CancellationToken.None);

        Assert.Equal(secondModel.Id, result?.Id);
    }

    [Fact]
    public async Task SaveChangesAsync_WithStaleUpdatedAt_RejectsConcurrentModelUpdate()
    {
        await using SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .Options;
        await using (SlicerDbContext setup = new(options))
        {
            _ = await setup.Database.EnsureCreatedAsync();
            setup.Models3D.Add(CreateModel(Guid.NewGuid(), clientUploadId: null));
            _ = await setup.SaveChangesAsync();
        }

        await using SlicerDbContext firstContext = new(options);
        await using SlicerDbContext staleContext = new(options);
        Model3D first = await firstContext.Models3D.SingleAsync();
        Model3D stale = await staleContext.Models3D.SingleAsync();
        first.ThumbnailFileName = "first.png";
        first.UpdatedAt = first.UpdatedAt.AddSeconds(1);
        _ = await firstContext.SaveChangesAsync();
        stale.ThumbnailFileName = "stale.png";
        stale.UpdatedAt = stale.UpdatedAt.AddSeconds(2);

        _ = await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());
    }

    private static Model3D CreateModel(Guid? userId, Guid? clientUploadId)
    {
        Guid id = Guid.NewGuid();
        return new Model3D
        {
            Id = id,
            FileName = $"{id}.stl",
            FilePath = "/",
            FileHash = Guid.NewGuid().ToString("N"),
            ClientUploadHash = clientUploadId.HasValue ? Guid.NewGuid().ToString("N") : null,
            FileSizeBytes = 1,
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            UploadedByUserId = userId,
            ClientUploadId = clientUploadId,
            IsValid = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
