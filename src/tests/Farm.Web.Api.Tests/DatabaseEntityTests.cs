using System;
using Farm.Web.Shared;
using System.Linq;
using System.Threading.Tasks;
using Farm.Infrastructure.Data;
using Farm.Infrastructure.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Farm.Web.Api.Tests;

/// <summary>
/// Tests for new database entities and their EF Core configuration
/// </summary>
[Trait("Category", "DbHeavy")]
[Collection("DbHeavySerial")]
[TestTiming("DbHeavy")]
public class DatabaseEntityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DatabaseEntityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var descriptor = services.FirstOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }
                var connection = new SqliteConnection("DataSource=:memory:");
                connection.Open();
                services.AddSingleton(connection);
                services.AddDbContext<AppDbContext>(options => options.UseSqlite(connection));
                // Ensure schema is created for the shared connection
                using var provider = services.BuildServiceProvider();
                using var scope = provider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                dbContext.Database.EnsureCreated();
            });
        });
    }

    [Fact]
    public async Task Model3D_ShouldCreateAndRetrieve_WithAllPropertiesAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test-model.stl",
            DisplayName = "Test Model",
            FilePath = "/tmp/test-model.stl",
            FileSizeBytes = 1024768,
            FileHash = "abc123def456",
            FileFormat = ModelFileFormat.STL,
            UploadedAt = DateTime.UtcNow,
            Description = "A test 3D model",
            Tags = "[\"test\", \"cube\"]",
            DimensionX = 20.5,
            DimensionY = 30.2,
            DimensionZ = 15.8,
            VolumeM3 = 9750.5,
            TriangleCount = 12,
            IsValid = true,
            ThumbnailPath = "/tmp/test-model-thumb.png",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.Models3D.Add(model);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.Models3D.FirstOrDefaultAsync(m => m.Id == model.Id);
        retrieved.Should().NotBeNull();
        retrieved!.OriginalFileName.Should().Be("test-model.stl");
        retrieved.DisplayName.Should().Be("Test Model");
        retrieved.FilePath.Should().Be("/tmp/test-model.stl");
        retrieved.FileSizeBytes.Should().Be(1024768);
        retrieved.FileHash.Should().Be("abc123def456");
        retrieved.FileFormat.Should().Be(ModelFileFormat.STL);
        retrieved.Description.Should().Be("A test 3D model");
        retrieved.Tags.Should().Be("[\"test\", \"cube\"]");
        retrieved.DimensionX.Should().Be(20.5);
        retrieved.DimensionY.Should().Be(30.2);
        retrieved.DimensionZ.Should().Be(15.8);
        retrieved.VolumeM3.Should().Be(9750.5);
        retrieved.TriangleCount.Should().Be(12);
        retrieved.IsValid.Should().BeTrue();
        retrieved.ThumbnailPath.Should().Be("/tmp/test-model-thumb.png");
    }

    // ...existing code for all other test methods...
    [Fact]
    public async Task SlicerProfile_ShouldCreateAndRetrieve_WithAllPropertiesAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = new SlicerProfile
        {
            Id = Guid.NewGuid(),
            Name = "Test Profile",
            Description = "A test slicer profile",
            SlicerType = SlicerType.PrusaSlicer,
            LayerHeight = 0.2,
            InfillPercentage = 25,
            PrintSpeed = 50.5,
            NozzleTemperature = 210,
            BedTemperature = 60,
            EnableSupports = true,
            Material = "PETG",
            Quality = ProfileQuality.Fine,
            AdvancedSettings = "{\"retraction_distance\": 2.0}",
            IsDefault = false,
            IsPublic = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.SlicerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Test Profile");
        retrieved.Description.Should().Be("A test slicer profile");
        retrieved.SlicerType.Should().Be(SlicerType.PrusaSlicer);
        retrieved.LayerHeight.Should().Be(0.2);
        retrieved.InfillPercentage.Should().Be(25);
        retrieved.PrintSpeed.Should().Be(50.5);
        retrieved.NozzleTemperature.Should().Be(210);
        retrieved.BedTemperature.Should().Be(60);
        retrieved.EnableSupports.Should().BeTrue();
        retrieved.Material.Should().Be("PETG");
        retrieved.Quality.Should().Be(ProfileQuality.Fine);
        retrieved.AdvancedSettings.Should().Be("{\"retraction_distance\": 2.0}");
        retrieved.IsDefault.Should().BeFalse();
        retrieved.IsPublic.Should().BeTrue();
    }

    [Fact]
    public async Task PrintJob_ShouldCreateAndRetrieve_WithAllPropertiesAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Create prerequisites
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Test Printer",
            ServerUrl = "http://test-printer:7125",
            Backend = 0
        };
        dbContext.Printers.Add(printer);

        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "test.gcode",
            DisplayName = "Test GCode",
            FilePath = "/tmp/test.gcode",
            FileSizeBytes = 2048,
            FileHash = "gcode123hash456",
            Source = GcodeSource.Upload,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.GcodeFiles.Add(gcodeFile);

        var printJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Test Print Job",
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            Priority = 5,
            QueuePosition = 1,
            RequiredNozzleDiameter = 0.4m,
            RequiredMaterialType = "PLA",
            RequiredCapabilities = ["heated_bed", "auto_leveling"],
            EstimatedPrintTime = TimeSpan.FromHours(2.5),
            EstimatedFilamentUsage = 25.5,
            PreferredPrinterIds = [printer.Id],
            ExcludedPrinterIds = [Guid.NewGuid()],
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };

        // Act
        dbContext.PrintJobs.Add(printJob);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.PrintJobs
            .Include(j => j.GcodeFile)
            .Include(j => j.AssignedPrinter)
            .FirstOrDefaultAsync(j => j.Id == printJob.Id);

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Test Print Job");
        retrieved.GcodeFileId.Should().Be(gcodeFile.Id);
        retrieved.AssignedPrinterId.Should().Be(printer.Id);
        retrieved.Status.Should().Be(PrintJobStatus.Queued);
        retrieved.Priority.Should().Be(5);
        retrieved.QueuePosition.Should().Be(1);
        retrieved.RequiredNozzleDiameter.Should().Be(0.4m);
        retrieved.RequiredMaterialType.Should().Be("PLA");
        retrieved.RequiredCapabilities.Should().BeEquivalentTo(["heated_bed", "auto_leveling"]);
        retrieved.EstimatedPrintTime.Should().Be(TimeSpan.FromHours(2.5));
        retrieved.EstimatedFilamentUsage.Should().Be(25.5);
        retrieved.PreferredPrinterIds.Should().BeEquivalentTo(new[] { printer.Id });
        retrieved.ExcludedPrinterIds.Should().HaveCount(1);

        // Test navigation properties
        retrieved.GcodeFile.Should().NotBeNull();
        retrieved.GcodeFile.DisplayName.Should().Be("Test GCode");
        retrieved.AssignedPrinter.Should().NotBeNull();
        retrieved.AssignedPrinter!.Name.Should().Be("Test Printer");
    }

    [Fact]
    public async Task PrinterCapabilities_ShouldCreateAndRetrieve_WithAllPropertiesAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Create prerequisite printer
        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Capabilities Test Printer",
            ServerUrl = "http://test-printer:7125",
            Backend = 0
        };
        dbContext.Printers.Add(printer);

        var capabilities = new PrinterCapabilities
        {
            Id = Guid.NewGuid(),
            PrinterId = printer.Id,
            NozzleDiameter = 0.6,
            SupportedMaterials = ["PLA", "PETG", "ABS", "TPU"],
            MaxBuildVolumeX = 220.0,
            MaxBuildVolumeY = 220.0,
            MaxBuildVolumeZ = 250.0,
            HasHeatedBed = true,
            HasEnclosure = true,
            MultiMaterial = false,
            NumberOfExtruders = 1,
            MinHotendTemp = 180,
            MaxHotendTemp = 280,
            MinBedTemp = 0,
            MaxBedTemp = 100,
            CurrentMaterial = "PLA",
            CurrentSpoolId = 42,
            IsAvailable = true,
            SupportsAutoLeveling = true,
            MaxPrintSpeed = 150,
            LastUpdated = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.PrinterCapabilities.Add(capabilities);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.PrinterCapabilities
            .Include(c => c.Printer)
            .FirstOrDefaultAsync(c => c.Id == capabilities.Id);

        retrieved.Should().NotBeNull();
        retrieved!.PrinterId.Should().Be(printer.Id);
        retrieved.NozzleDiameter.Should().Be(0.6);
        retrieved.SupportedMaterials.Should().BeEquivalentTo(["PLA", "PETG", "ABS", "TPU"]);
        retrieved.MaxBuildVolumeX.Should().Be(220.0);
        retrieved.MaxBuildVolumeY.Should().Be(220.0);
        retrieved.MaxBuildVolumeZ.Should().Be(250.0);
        retrieved.HasHeatedBed.Should().BeTrue();
        retrieved.HasEnclosure.Should().BeTrue();
        retrieved.MultiMaterial.Should().BeFalse();
        retrieved.NumberOfExtruders.Should().Be(1);
        retrieved.MinHotendTemp.Should().Be(180);
        retrieved.MaxHotendTemp.Should().Be(280);
        retrieved.MinBedTemp.Should().Be(0);
        retrieved.MaxBedTemp.Should().Be(100);
        retrieved.CurrentMaterial.Should().Be("PLA");
        retrieved.CurrentSpoolId.Should().Be(42);
        retrieved.IsAvailable.Should().BeTrue();
        retrieved.SupportsAutoLeveling.Should().BeTrue();
        retrieved.MaxPrintSpeed.Should().Be(150);

        // Test navigation property
        retrieved.Printer.Should().NotBeNull();
        retrieved.Printer.Name.Should().Be("Capabilities Test Printer");
    }

    [Theory]
    [InlineData(SlicerType.PrusaSlicer)]
    [InlineData(SlicerType.OrcaSlicer)]
    [InlineData(SlicerType.Cura)]
    [InlineData(SlicerType.SuperSlicer)]
    public async Task SlicerProfile_ShouldSupportAllSlicerTypesAsync(SlicerType slicerType)
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = new SlicerProfile
        {
            Id = Guid.NewGuid(),
            Name = $"Test {slicerType} Profile",
            SlicerType = slicerType,
            Quality = ProfileQuality.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.SlicerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id);
        retrieved.Should().NotBeNull();
        retrieved!.SlicerType.Should().Be(slicerType);
        retrieved.Name.Should().Be($"Test {slicerType} Profile");
    }

    [Theory]
    [InlineData(ProfileQuality.Draft)]
    [InlineData(ProfileQuality.Standard)]
    [InlineData(ProfileQuality.Fine)]
    public async Task SlicerProfile_ShouldSupportAllQualityLevelsAsync(ProfileQuality quality)
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var profile = new SlicerProfile
        {
            Id = Guid.NewGuid(),
            Name = $"Test {quality} Profile",
            SlicerType = SlicerType.PrusaSlicer,
            Quality = quality,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.SlicerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.SlicerProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id);
        retrieved.Should().NotBeNull();
        retrieved!.Quality.Should().Be(quality);
    }

    [Theory]
    [InlineData(ModelFileFormat.STL)]
    [InlineData(ModelFileFormat.TMF)]
    [InlineData(ModelFileFormat.OBJ)]
    [InlineData(ModelFileFormat.PLY)]
    [InlineData(ModelFileFormat.STEP)]
    public async Task Model3D_ShouldSupportAllFileFormatsAsync(ModelFileFormat format)
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var model = new Model3D
        {
            Id = Guid.NewGuid(),
            OriginalFileName = $"test.{format.ToString().ToLowerInvariant()}",
            DisplayName = $"Test {format} Model",
            FilePath = $"/tmp/test.{format.ToString().ToLowerInvariant()}",
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString("N"),
            FileFormat = format,
            UploadedAt = DateTime.UtcNow,
            IsValid = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.Models3D.Add(model);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.Models3D.FirstOrDefaultAsync(m => m.Id == model.Id);
        retrieved.Should().NotBeNull();
        retrieved!.FileFormat.Should().Be(format);
    }

    [Fact]
    public async Task PrintJob_ShouldSupportAllStatuses_BatchedLoop()
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var printer = new Printer { Id = Guid.NewGuid(), Name = "Status Test Printer", ServerUrl = "http://test-printer:7125", Backend = 0 };
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "status-test.gcode",
            DisplayName = "Status Test GCode",
            FilePath = "/tmp/status-test.gcode",
            FileSizeBytes = 1024,
            FileHash = Guid.NewGuid().ToString("N"),
            Source = GcodeSource.Upload,
            UploadedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        dbContext.Printers.Add(printer);
        dbContext.GcodeFiles.Add(gcodeFile);
        await dbContext.SaveChangesAsync();

        // Loop statuses on a single job (update + save) to avoid full object graph re-creation.
        var jobId = Guid.NewGuid();
        var job = new PrintJob
        {
            Id = jobId,
            Name = "Status Loop Job",
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };
        dbContext.PrintJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var statuses = new[]
        {
            PrintJobStatus.Queued,
            PrintJobStatus.Assigned,
            PrintJobStatus.Starting,
            PrintJobStatus.Printing,
            PrintJobStatus.Paused,
            PrintJobStatus.Completed,
            PrintJobStatus.Failed,
            PrintJobStatus.Cancelled
        };

        foreach (var s in statuses)
        {
            job.Status = s;
            job.UpdatedAt = DateTime.UtcNow;
            await dbContext.SaveChangesAsync();
            var retrieved = await dbContext.PrintJobs.FirstOrDefaultAsync(j => j.Id == jobId);
            retrieved.Should().NotBeNull();
            retrieved!.Status.Should().Be(s);
        }
    }

    [Fact]
    public async Task SlicerProfile_ShouldSupportPrinterModelAssociationAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Create manufacturer and printer model
        var manufacturer = new Manufacturer
        {
            Id = Guid.NewGuid(),
            Name = "Test Manufacturer"
        };
        dbContext.Manufacturers.Add(manufacturer);

        var printerModel = new PrinterModel
        {
            Id = Guid.NewGuid(),
            Name = "Test Printer Model",
            ManufacturerId = manufacturer.Id
        };
        dbContext.Models.Add(printerModel);

        var profile = new SlicerProfile
        {
            Id = Guid.NewGuid(),
            Name = "Model-Specific Profile",
            SlicerType = SlicerType.PrusaSlicer,
            PrinterModelId = printerModel.Id,
            Quality = ProfileQuality.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.SlicerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.SlicerProfiles
            .Include(p => p.PrinterModel)
            .ThenInclude(m => m!.Manufacturer)
            .FirstOrDefaultAsync(p => p.Id == profile.Id);

        retrieved.Should().NotBeNull();
        retrieved!.PrinterModelId.Should().Be(printerModel.Id);
        retrieved.PrinterModel.Should().NotBeNull();
        retrieved.PrinterModel!.Name.Should().Be("Test Printer Model");
        retrieved.PrinterModel.Manufacturer.Should().NotBeNull();
        retrieved.PrinterModel.Manufacturer!.Name.Should().Be("Test Manufacturer");
    }

    [Fact]
    public async Task SlicerProfile_ShouldSupportSpecificPrinterAssociationAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var printer = new Printer
        {
            Id = Guid.NewGuid(),
            Name = "Specific Test Printer",
            ServerUrl = "http://specific-printer:7125",
            Backend = 0
        };
        dbContext.Printers.Add(printer);

        var profile = new SlicerProfile
        {
            Id = Guid.NewGuid(),
            Name = "Printer-Specific Profile",
            SlicerType = SlicerType.PrusaSlicer,
            SpecificPrinterId = printer.Id,
            Quality = ProfileQuality.Standard,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        // Act
        dbContext.SlicerProfiles.Add(profile);
        await dbContext.SaveChangesAsync();

        // Assert
        var retrieved = await dbContext.SlicerProfiles
            .Include(p => p.SpecificPrinter)
            .FirstOrDefaultAsync(p => p.Id == profile.Id);

        retrieved.Should().NotBeNull();
        retrieved!.SpecificPrinterId.Should().Be(printer.Id);
        retrieved.SpecificPrinter.Should().NotBeNull();
        retrieved.SpecificPrinter!.Name.Should().Be("Specific Test Printer");
    }

    [Fact]
    public async Task DatabaseContext_ShouldHandleComplexRelationshipsAsync()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Create a complete workflow: Manufacturer -> Model -> Printer -> Capabilities -> GCode -> PrintJob
        var manufacturer = new Manufacturer { Id = Guid.NewGuid(), Name = "Complex Test Mfg" };
        var printerModel = new PrinterModel { Id = Guid.NewGuid(), Name = "Complex Model", ManufacturerId = manufacturer.Id };
        var printer = new Printer { Id = Guid.NewGuid(), Name = "Complex Printer", ModelId = printerModel.Id, ServerUrl = "http://complex:7125", Backend = 0 };
        var capabilities = new PrinterCapabilities { Id = Guid.NewGuid(), PrinterId = printer.Id, IsAvailable = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        var gcodeFile = new GcodeFile
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "complex.gcode",
            DisplayName = "Complex GCode",
            FilePath = "/tmp/complex.gcode",
            FileSizeBytes = 4096,
            FileHash = "complex123hash",
            Source = GcodeSource.Upload,
            UploadedAt = DateTime.UtcNow,
            TargetPrinterId = printer.Id,
            TargetModelId = printerModel.Id,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        var printJob = new PrintJob
        {
            Id = Guid.NewGuid(),
            Name = "Complex Job",
            GcodeFileId = gcodeFile.Id,
            AssignedPrinterId = printer.Id,
            Status = PrintJobStatus.Queued,
            Priority = 1,
            QueuePosition = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            QueuedAt = DateTime.UtcNow
        };

        // Act
        dbContext.Manufacturers.Add(manufacturer);
        dbContext.Models.Add(printerModel);
        dbContext.Printers.Add(printer);
        dbContext.PrinterCapabilities.Add(capabilities);
        dbContext.GcodeFiles.Add(gcodeFile);
        dbContext.PrintJobs.Add(printJob);
        await dbContext.SaveChangesAsync();

        // Assert - Load with all navigation properties
        var retrievedJob = await dbContext.PrintJobs
            .Include(j => j.GcodeFile)
            .ThenInclude(g => g.TargetPrinter)
            .ThenInclude(p => p!.Model)
            .ThenInclude(m => m!.Manufacturer)
            .Include(j => j.AssignedPrinter)
            .ThenInclude(p => p!.Capabilities)
            .FirstOrDefaultAsync(j => j.Id == printJob.Id);

        retrievedJob.Should().NotBeNull();
        retrievedJob!.GcodeFile.TargetPrinter!.Model!.Manufacturer!.Name.Should().Be("Complex Test Mfg");
        retrievedJob.AssignedPrinter!.Capabilities!.IsAvailable.Should().BeTrue();
    }
}
