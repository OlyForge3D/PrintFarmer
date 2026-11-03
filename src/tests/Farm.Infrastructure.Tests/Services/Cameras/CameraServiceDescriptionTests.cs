using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Cameras;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Services.Cameras;
using Farm.Infrastructure.Services.Printers;
using Farm.Infrastructure.Services.StorageManagement;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Farm.Infrastructure.Tests.Services.Cameras;

public sealed class CameraServiceDescriptionTests : IAsyncDisposable
{
    private const string OriginalDescription = "Camera from vt01 printer";
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"CameraServiceDescriptionTests_{Guid.NewGuid():N}")
        .Options);

    public async ValueTask DisposeAsync() => await _db.DisposeAsync();

    [Theory]
    [InlineData("", null)]
    [InlineData(" \t\r\n ", null)]
    [InlineData("  Workshop camera  ", "Workshop camera")]
    [InlineData(null, OriginalDescription)]
    public async Task UpdateAsync_ExplicitDescription_PersistsExpectedValue(string? description, string? expected)
    {
        await AssertDescriptionUpdateAsync(new UpdateCameraDto { Description = description }, expected);
    }

    [Fact]
    public async Task UpdateAsync_OmittedDescription_PreservesExistingValue()
    {
        await AssertDescriptionUpdateAsync(new UpdateCameraDto { Name = "Renamed camera" }, OriginalDescription);
    }

    private async Task AssertDescriptionUpdateAsync(UpdateCameraDto request, string? expected)
    {
        var camera = new Camera
        {
            Id = Guid.NewGuid(),
            Name = "vt01 Camera",
            Description = OriginalDescription,
            StreamUrl = "http://camera.local/stream",
            Source = CameraSource.Standalone,
        };
        _db.Cameras.Add(camera);
        await _db.SaveChangesAsync();

        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.Cameras).Returns(new EfCameraRepository(_db));
        unitOfWork.Setup(u => u.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => _db.SaveChangesAsync(ct));
        var service = new CameraService(
            unitOfWork.Object,
            _db,
            NullLogger<CameraService>.Instance,
            Mock.Of<IPrintersService>(),
            Mock.Of<IStoragePathService>());

        CameraDto? updated = await service.UpdateAsync(camera.Id, request, CancellationToken.None);

        updated.Should().NotBeNull();
        updated!.Description.Should().Be(expected);
        _db.ChangeTracker.Clear();
        Camera persisted = await _db.Cameras.SingleAsync(c => c.Id == camera.Id);
        persisted.Description.Should().Be(expected);
    }
}
