using Farm.Infrastructure.Domain;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Tests.TestInfrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Farm.Slicer.Module.Tests.Data;

public class Model3DPickerProjectionTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(100)]
    [InlineData(1000)]
    public async Task ListValidPickerAsync_ProjectsValidSharedModelsInUploadOrder_WithOneSelect(int modelCount)
    {
        await using SqliteConnection connection = new("DataSource=:memory:");
        await connection.OpenAsync();
        CountingDbCommandInterceptor interceptor = new();
        DbContextOptions<SlicerDbContext> options = new DbContextOptionsBuilder<SlicerDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using SlicerDbContext db = new(options);
        _ = await db.Database.EnsureCreatedAsync();
        DateTime baseUploadedAt = new(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Guid firstUploaderId = Guid.NewGuid();
        Guid secondUploaderId = Guid.NewGuid();
        Model3D[] models = Enumerable.Range(0, modelCount)
            .Select(index => CreateModel(
                $"model-{index:D4}.3mf",
                index % 2 == 0 ? firstUploaderId : secondUploaderId,
                baseUploadedAt.AddMinutes(index),
                index % 2 == 0 ? ModelFileFormat.TMF : ModelFileFormat.STL))
            .Append(CreateModel("invalid-model.stl", Guid.NewGuid(), baseUploadedAt.AddDays(1), isValid: false))
            .ToArray();
        db.Models3D.AddRange(models);
        _ = await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        interceptor.Reset();
        EfModel3DFileRepository repository = new(db);

        IReadOnlyList<Model3DPickerProjection> result =
            await repository.ListValidPickerAsync(CancellationToken.None);

        Assert.Equal(modelCount, result.Count);
        string query = Assert.Single(interceptor.Commands);
        Assert.Contains("SELECT", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FilePath", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ExtractedMetadataJson", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SourceUrl", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SourceLicense", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SourceCreator", query, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(db.ChangeTracker.Entries<Model3D>());

        Assert.Equal($"model-{modelCount - 1:D4}.3mf", result[0].FileName);
        Assert.Equal(baseUploadedAt.AddMinutes(modelCount - 1), result[0].UploadedAt);
        Assert.Equal(baseUploadedAt, result[^1].UploadedAt);
        Assert.Equal(ModelFileFormat.TMF, result.Single(model => model.FileName == "model-0000.3mf").FileFormat);
        Assert.DoesNotContain(result, model => model.FileName == "invalid-model.stl");
        Assert.Equal(firstUploaderId, models[0].UploadedByUserId);
        Assert.Equal(secondUploaderId, models[1].UploadedByUserId);
    }

    private static Model3D CreateModel(
        string fileName,
        Guid uploadedByUserId,
        DateTime uploadedAt,
        ModelFileFormat fileFormat = ModelFileFormat.STL,
        bool isValid = true)
    {
        Guid id = Guid.NewGuid();
        return new Model3D
        {
            Id = id,
            Name = fileName,
            FileName = fileName,
            FilePath = "/models",
            FileHash = id.ToString("N"),
            FileSizeBytes = 1,
            FileFormat = fileFormat,
            UploadedAt = uploadedAt,
            UploadedByUserId = uploadedByUserId,
            IsValid = isValid,
            ExtractedMetadataJson = "{\"metadata\":\"not selected\"}",
            SourceUrl = "https://example.test/model",
            SourceLicense = "CC BY 4.0",
            SourceCreator = "test",
            CreatedAt = uploadedAt,
            UpdatedAt = uploadedAt,
        };
    }

}
