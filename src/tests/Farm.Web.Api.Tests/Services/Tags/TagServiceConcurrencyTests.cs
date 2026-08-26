using Farm.Infrastructure;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Dtos;
using Farm.Infrastructure.Exceptions;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Services.Tags;
using Farm.Web.Api.Tests.TestInfrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Farm.Web.Api.Tests.Services.Tags;

/// <summary>
/// Tests for tag revision and optimistic-concurrency semantics (#844): create seeds revision 1,
/// updates bump the revision and rotate the concurrency token, stale writes are rejected with
/// <see cref="TagConcurrencyException"/>, and rename collisions surface as
/// <see cref="DuplicateEntityException"/>. Backend behavior is provider-agnostic; tests run over
/// a SQLite in-memory database.
/// </summary>
public class TagServiceConcurrencyTests
{
    private static TagService CreateService(AppDbContext db)
    {
        var repo = new EfTagRepository(db);
        return new TagService(repo, NullLogger<TagService>.Instance);
    }

    [Fact]
    public async Task Create_SeedsRevisionOne_AndConcurrencyToken()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        TagDto created = await service.CreateTagAsync(new CreateTagDto { Name = "alpha" }, CancellationToken.None);

        Assert.Equal(1, created.Revision);
        Assert.NotEqual(Guid.Empty, created.ConcurrencyToken);
    }

    [Fact]
    public async Task Update_BumpsRevision_AndRotatesToken()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto created = await service.CreateTagAsync(new CreateTagDto { Name = "alpha" }, CancellationToken.None);

        TagDto updated = await service.UpdateTagAsync(
            created.Id,
            new UpdateTagDto { Description = "d", ExpectedRevision = created.Revision },
            CancellationToken.None);

        Assert.Equal(2, updated.Revision);
        Assert.NotEqual(created.ConcurrencyToken, updated.ConcurrencyToken);
        Assert.Equal("d", updated.Description);
    }

    [Fact]
    public async Task Update_WithStaleRevision_ThrowsConcurrency()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto created = await service.CreateTagAsync(new CreateTagDto { Name = "alpha" }, CancellationToken.None);

        TagConcurrencyException ex = await Assert.ThrowsAsync<TagConcurrencyException>(() =>
            service.UpdateTagAsync(
                created.Id,
                new UpdateTagDto { Description = "d", ExpectedRevision = 99 },
                CancellationToken.None));

        Assert.Equal(99L, ex.ExpectedRevision!.Value);
        Assert.Equal(created.Revision, ex.ActualRevision!.Value);
    }

    [Fact]
    public async Task Update_LostUpdate_SecondWriterRejected()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        TagDto created = await service.CreateTagAsync(new CreateTagDto { Name = "alpha" }, CancellationToken.None);

        // Two writers both observe revision 1.
        long baseRevision = created.Revision;

        // First writer commits, advancing the revision to 2.
        _ = await service.UpdateTagAsync(
            created.Id,
            new UpdateTagDto { Description = "first", ExpectedRevision = baseRevision },
            CancellationToken.None);

        // Second writer's expected revision is now stale → rejected.
        _ = await Assert.ThrowsAsync<TagConcurrencyException>(() =>
            service.UpdateTagAsync(
                created.Id,
                new UpdateTagDto { Description = "second", ExpectedRevision = baseRevision },
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_RenameToExistingName_ThrowsDuplicate()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);
        _ = await service.CreateTagAsync(new CreateTagDto { Name = "alpha" }, CancellationToken.None);
        TagDto beta = await service.CreateTagAsync(new CreateTagDto { Name = "beta" }, CancellationToken.None);

        _ = await Assert.ThrowsAsync<DuplicateEntityException>(() =>
            service.UpdateTagAsync(
                beta.Id,
                new UpdateTagDto { Name = "alpha", ExpectedRevision = beta.Revision },
                CancellationToken.None));
    }

    [Fact]
    public async Task Update_MissingTag_ThrowsNotFound()
    {
        using AppDbContext db = AppDbTestHelpers.CreateSqliteInMemoryDb();
        TagService service = CreateService(db);

        _ = await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.UpdateTagAsync(
                Guid.NewGuid(),
                new UpdateTagDto { Name = "x", ExpectedRevision = 1 },
                CancellationToken.None));
    }
}
