using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Services.Models;
using Farm.Infrastructure.Services.StorageManagement;
using Farm.Slicer.Module.Api.HostedServices;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Farm.Slicer.Module.Tests.HostedServices;

/// <summary>
/// Tests for the backfill that populates geometry metadata for models uploaded before real
/// analysis existed (#1814), so the library reflects real data without requiring a re-upload.
/// </summary>
public class ModelMetadataBackfillServiceTests : IDisposable
{
    private readonly string _modelsDir = Path.Join(Path.GetTempPath(), "pfarm-backfill-tests", Guid.NewGuid().ToString());
    private readonly List<ServiceProvider> _serviceProviders = [];

    public ModelMetadataBackfillServiceTests()
    {
        Directory.CreateDirectory(_modelsDir);
    }

    public void Dispose()
    {
        foreach (ServiceProvider provider in _serviceProviders)
        {
            provider.Dispose();
        }

        try
        {
            if (Directory.Exists(_modelsDir))
            {
                Directory.Delete(_modelsDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort cleanup only.
        }

        GC.SuppressFinalize(this);
    }

    private Model3D CreateModelOnDisk(string fileName, ModelFileFormat format = ModelFileFormat.STL)
    {
        File.WriteAllBytes(Path.Join(_modelsDir, fileName), new byte[] { 1, 2, 3 });
        return new Model3D
        {
            Id = Guid.NewGuid(),
            FileName = fileName,
            FileFormat = format,
            IsValid = true,
            UploadedAt = DateTime.UtcNow,
        };
    }

    [Fact]
    public async Task BackfillAsync_ModelNeedsAnalysis_UpdatesGeometryAndSaves()
    {
        Model3D model = CreateModelOnDisk("needs-analysis.stl");

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        _ = repo.SetupSequence(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D> { model })
            .ReturnsAsync(new List<Model3D>());
        _ = repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);
        _ = analysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), ".stl", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelAnalysisResult(10, 20, 30, 100, IsValid: true, ValidationErrors: null));

        ModelMetadataBackfillService svc = CreateService(repo, analysis, out _);

        await svc.BackfillAsync(CancellationToken.None);

        Assert.Equal(10, model.DimensionX);
        Assert.Equal(20, model.DimensionY);
        Assert.Equal(30, model.DimensionZ);
        Assert.Equal(100, model.TriangleCount);
        Assert.True(model.IsValid);
        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BackfillAsync_NoModelsNeedAnalysis_DoesNotSave()
    {
        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        _ = repo.Setup(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D>());

        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);

        ModelMetadataBackfillService svc = CreateService(repo, analysis, out _);

        await svc.BackfillAsync(CancellationToken.None);

        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A model whose file is missing from disk must not be retried on every future start: it is
    /// marked unreadable (TriangleCount = 0, IsValid = false) so it drops out of the
    /// "still needs analysis" query, and the batch continues with the remaining rows.
    /// </summary>
    [Fact]
    public async Task BackfillAsync_FileMissingOnDisk_MarksUnanalyzableAndContinuesBatch()
    {
        Model3D missing = new()
        {
            Id = Guid.NewGuid(),
            FileName = "does-not-exist.stl",
            FileFormat = ModelFileFormat.STL,
            IsValid = true,
            UploadedAt = DateTime.UtcNow,
        };
        Model3D present = CreateModelOnDisk("present.stl");

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        _ = repo.SetupSequence(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D> { missing, present })
            .ReturnsAsync(new List<Model3D>());
        _ = repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);
        _ = analysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), ".stl", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ModelAnalysisResult(5, 5, 5, 10, IsValid: true, ValidationErrors: null));

        ModelMetadataBackfillService svc = CreateService(repo, analysis, out _);

        await svc.BackfillAsync(CancellationToken.None);

        Assert.Equal(0, missing.TriangleCount);
        Assert.False(missing.IsValid);
        Assert.NotNull(missing.ValidationErrors);

        Assert.Equal(10, present.TriangleCount);
        Assert.True(present.IsValid);
    }

    /// <summary>
    /// An analysis failure for one row must not abort the whole batch, and must never crash the
    /// host (.NET's default BackgroundServiceExceptionBehavior is StopHost).
    /// </summary>
    [Fact]
    public async Task BackfillAsync_AnalysisThrowsForOneRow_MarksThatRowUnanalyzableAndDoesNotThrow()
    {
        Model3D model = CreateModelOnDisk("throws.stl");

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        _ = repo.SetupSequence(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D> { model })
            .ReturnsAsync(new List<Model3D>());
        _ = repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);
        _ = analysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), ".stl", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        ModelMetadataBackfillService svc = CreateService(repo, analysis, out _);

        await svc.BackfillAsync(CancellationToken.None);

        Assert.Equal(0, model.TriangleCount);
        Assert.False(model.IsValid);
        repo.Verify(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// Regression (#1814 review): a null analysis result (unsupported format) must keep the same
    /// "unknown, not invalid" contract as the upload path — it should not flip a previously-valid
    /// row to IsValid=false, only mark it as no-longer-needing-analysis via TriangleCount.
    /// </summary>
    [Fact]
    public async Task BackfillAsync_AnalysisReturnsNull_KeepsIsValidTrueButStopsRetrying()
    {
        Model3D model = CreateModelOnDisk("unsupported.stl");
        model.IsValid = true;

        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        _ = repo.SetupSequence(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<Model3D> { model })
            .ReturnsAsync(new List<Model3D>());
        _ = repo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);
        _ = analysis.Setup(a => a.AnalyzeModelAsync(It.IsAny<string>(), ".stl", It.IsAny<CancellationToken>()))
            .ReturnsAsync((ModelAnalysisResult?)null);

        ModelMetadataBackfillService svc = CreateService(repo, analysis, out _);

        await svc.BackfillAsync(CancellationToken.None);

        Assert.Equal(0, model.TriangleCount);
        Assert.True(model.IsValid);
        Assert.Null(model.DimensionX);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledByConfiguration_DoesNotRunOnStart()
    {
        Mock<IModel3DFileRepository> repo = new(MockBehavior.Strict);
        Mock<IModelAnalysisService> analysis = new(MockBehavior.Strict);

        ModelMetadataBackfillService svc = CreateService(
            repo,
            analysis,
            out _,
            new Dictionary<string, string?> { ["ModelMetadataBackfill:Enabled"] = "false" });

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        repo.Verify(r => r.ListNeedingAnalysisAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private ModelMetadataBackfillService CreateService(
        Mock<IModel3DFileRepository> repo,
        Mock<IModelAnalysisService> analysis,
        out ServiceProvider provider,
        Dictionary<string, string?>? config = null)
    {
        Mock<IStoragePathService> storagePath = new(MockBehavior.Strict);
        _ = storagePath.Setup(s => s.GetModelUploadDirectory()).Returns(_modelsDir);

        ServiceCollection services = new();
        _ = services.AddSingleton(repo.Object);
        _ = services.AddSingleton(analysis.Object);
        _ = services.AddSingleton(storagePath.Object);
        provider = services.BuildServiceProvider();
        _serviceProviders.Add(provider);

        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config ?? new Dictionary<string, string?>
            {
                // Keep the test fast: no startup delay.
                ["ModelMetadataBackfill:StartupDelaySeconds"] = "0",
                ["ModelMetadataBackfill:BatchSize"] = "50",
            })
            .Build();

        return new ModelMetadataBackfillService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<ModelMetadataBackfillService>.Instance,
            configuration);
    }
}
