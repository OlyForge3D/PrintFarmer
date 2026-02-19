using Farm.Slicer.Module.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Services;

public class SlicerModel3DQueryProviderTests : IDisposable
{
    private readonly SlicerDbContext _db = TestHelpers.CreateSqliteInMemoryDb();

    [Fact]
    public async Task ExistsAsync_ReturnsFalse_WhenNoModelsExist()
    {
        var sut = new SlicerModel3DQueryProvider(_db);

        bool exists = await sut.ExistsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(exists);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrue_WhenModelExists()
    {
        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = "test.stl",
            FilePath = "/models/test.stl",
            FileSizeBytes = 1024,
            UploadedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Set<Model3D>().Add(model);
        await _db.SaveChangesAsync();

        var sut = new SlicerModel3DQueryProvider(_db);

        bool exists = await sut.ExistsAsync(model.Id, CancellationToken.None);

        Assert.True(exists);
    }

    [Fact]
    public async Task GetAllIdsAsync_ReturnsEmpty_WhenNoModelsExist()
    {
        var sut = new SlicerModel3DQueryProvider(_db);

        IReadOnlyList<Guid> ids = await sut.GetAllIdsAsync(CancellationToken.None);

        Assert.Empty(ids);
    }

    [Fact]
    public async Task GetAllIdsAsync_ReturnsAllIds()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        _db.Set<Model3D>().AddRange(
            CreateModel3D(id1),
            CreateModel3D(id2));
        await _db.SaveChangesAsync();

        var sut = new SlicerModel3DQueryProvider(_db);

        IReadOnlyList<Guid> ids = await sut.GetAllIdsAsync(CancellationToken.None);

        Assert.Equal(2, ids.Count);
        Assert.Contains(id1, ids);
        Assert.Contains(id2, ids);
    }

    [Fact]
    public async Task GetLatestUpdatedAtAsync_ReturnsNull_WhenIdsEmpty()
    {
        var sut = new SlicerModel3DQueryProvider(_db);

        DateTime? result = await sut.GetLatestUpdatedAtAsync([], CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestUpdatedAtAsync_ReturnsNull_WhenNoMatchingModels()
    {
        var sut = new SlicerModel3DQueryProvider(_db);

        DateTime? result = await sut.GetLatestUpdatedAtAsync([Guid.NewGuid()], CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetLatestUpdatedAtAsync_ReturnsLatestDate()
    {
        var older = DateTime.UtcNow.AddDays(-5);
        var newer = DateTime.UtcNow;
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        _db.Set<Model3D>().AddRange(
            CreateModel3D(id1, older),
            CreateModel3D(id2, newer));
        await _db.SaveChangesAsync();

        var sut = new SlicerModel3DQueryProvider(_db);

        DateTime? result = await sut.GetLatestUpdatedAtAsync([id1, id2], CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(newer, result.Value, TimeSpan.FromSeconds(1));
    }

    private static Model3D CreateModel3D(Guid id, DateTime? updatedAt = null) => new()
    {
        Id = id,
        FileName = $"{id}.stl",
        FilePath = $"/models/{id}.stl",
        FileHash = id.ToString(),
        FileSizeBytes = 1024,
        UploadedAt = DateTime.UtcNow,
        UpdatedAt = updatedAt ?? DateTime.UtcNow,
    };

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
