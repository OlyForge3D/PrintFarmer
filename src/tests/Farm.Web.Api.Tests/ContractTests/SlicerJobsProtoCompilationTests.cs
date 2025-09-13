using Farm.Web.Api.Grpc;
using Google.Protobuf; // For ToByteArray extension methods

namespace Farm.Web.Api.Tests.ContractTests;

/// <summary>
/// Tests to validate gRPC protocol buffer definitions compile correctly
/// These tests ensure the proto/slicer_jobs.proto generates valid C# classes
/// </summary>
public class SlicerJobsProtoCompilationTests
{
    private static readonly string[] s_supportedFormats = ["stl", "3mf", "obj"];
    [Fact]
    public void SubmitJobRequest_CanBeCreatedAndSerialized()
    {
        // Arrange & Act - Create a gRPC request object
        var request = new SubmitJobRequest
        {
            JobId = Guid.NewGuid().ToString(),
            UserId = Guid.NewGuid().ToString(),
            PrinterId = Guid.NewGuid().ToString(),
            ModelFileUrl = "https://storage.example.com/models/test.stl",
            ModelFileName = "test.stl",
            SlicerEngine = SlicerEngineType.SlicerEngineOrca,
            SlicerProfile = new SlicerProfile
            {
                Name = "Standard PLA",
                Description = "General purpose PLA profile",
                LayerHeight = 0.2,
                InfillPercentage = 20,
                PrintSpeed = 50,
                NozzleTemperature = 210,
                BedTemperature = 60,
                EnableSupports = false,
                Material = "PLA",
                Quality = "Standard"
            },
            Priority = JobPriority.Normal,
            FileSizeBytes = 2048576,
            FileChecksum = "d41d8cd98f00b204e9800998ecf8427e"
        };

        request.Metadata.Add("originalFileName", "test-model.stl");
        request.Metadata.Add("clientVersion", "1.0.0");

        // Assert - Verify object was created successfully
        Assert.NotNull(request);
        Assert.False(string.IsNullOrEmpty(request.JobId));
        Assert.False(string.IsNullOrEmpty(request.ModelFileUrl));
        Assert.Equal(SlicerEngineType.SlicerEngineOrca, request.SlicerEngine);
        Assert.Equal(JobPriority.Normal, request.Priority);
        Assert.NotNull(request.SlicerProfile);
        Assert.Equal(2, request.Metadata.Count);

        // Test serialization roundtrip
        var bytes = request.ToByteArray();
        Assert.NotEmpty(bytes);

        var deserialized = SubmitJobRequest.Parser.ParseFrom(bytes);
        Assert.Equal(request.JobId, deserialized.JobId);
        Assert.Equal(request.ModelFileUrl, deserialized.ModelFileUrl);
        Assert.Equal(request.SlicerEngine, deserialized.SlicerEngine);
    }

    [Fact]
    public void GetJobStatusResponse_CanBeCreatedAndSerialized()
    {
        // Arrange & Act
        var response = new GetJobStatusResponse
        {
            JobId = Guid.NewGuid().ToString(),
            Status = JobStatus.Slicing,
            ProgressPercentage = 45,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds(),
            WorkerId = "worker-01",
            EstimatedPrintTimeSeconds = 7200,
            EstimatedFilamentUsageGrams = 25.5,
            LayerCount = 450,
            RetryCount = 0
        };

        response.Metadata.Add("slicerVersion", "1.8.0");

        // Assert
        Assert.NotNull(response);
        Assert.Equal(JobStatus.Slicing, response.Status);
        Assert.Equal(45, response.ProgressPercentage);
        Assert.True(response.StartedAt > 0);
        Assert.Equal("worker-01", response.WorkerId);
        Assert.Single(response.Metadata);

        // Test serialization
        var bytes = response.ToByteArray();
        Assert.NotEmpty(bytes);

        var deserialized = GetJobStatusResponse.Parser.ParseFrom(bytes);
        Assert.Equal(response.JobId, deserialized.JobId);
        Assert.Equal(response.Status, deserialized.Status);
        Assert.Equal(response.ProgressPercentage, deserialized.ProgressPercentage);
    }

    [Fact]
    public void ProgressUpdate_CanBeCreatedAndSerialized()
    {
        // Arrange & Act
        var update = new ProgressUpdate
        {
            JobId = Guid.NewGuid().ToString(),
            Status = JobStatus.Slicing,
            ProgressPercentage = 75,
            CurrentStep = "Generating toolpaths",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            EstimatedRemainingSeconds = 600
        };

        update.AdditionalData.Add("layersCompleted", "300");
        update.AdditionalData.Add("totalLayers", "400");

        // Assert
        Assert.NotNull(update);
        Assert.Equal(JobStatus.Slicing, update.Status);
        Assert.Equal(75, update.ProgressPercentage);
        Assert.Equal("Generating toolpaths", update.CurrentStep);
        Assert.Equal(600, update.EstimatedRemainingSeconds);
        Assert.Equal(2, update.AdditionalData.Count);

        // Test serialization
        var bytes = update.ToByteArray();
        Assert.NotEmpty(bytes);

        var deserialized = ProgressUpdate.Parser.ParseFrom(bytes);
        Assert.Equal(update.JobId, deserialized.JobId);
        Assert.Equal(update.CurrentStep, deserialized.CurrentStep);
        Assert.Equal(update.EstimatedRemainingSeconds, deserialized.EstimatedRemainingSeconds);
    }

    [Fact]
    public void WorkerInfo_CanBeCreatedAndSerialized()
    {
        // Arrange & Act
        var workerInfo = new WorkerInfo
        {
            WorkerId = "worker-01",
            WorkerName = "Primary Slicer Worker",
            WorkerUrl = "http://worker-01:8080",
            Status = WorkerStatus.Busy,
            LastHeartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Capabilities = new WorkerCapabilities
            {
                MaxConcurrentJobs = 4,
                MaxFileSizeBytes = 100_000_000,
                SupportsLargeModels = true,
                SupportsMultiMaterial = false,
                CpuArchitecture = "x64",
                MemoryMb = 8192,
                CpuCores = 8
            },
            Metrics = new WorkerMetrics
            {
                JobsCompletedToday = 25,
                JobsFailedToday = 2,
                AverageJobTimeSeconds = 1200,
                CpuUsagePercentage = 65.5,
                MemoryUsagePercentage = 42.3,
                DiskUsagePercentage = 15.7,
                UptimeSeconds = 86400
            }
        };

        workerInfo.SupportedEngines.Add(SlicerEngineType.SlicerEngineOrca);
        workerInfo.SupportedEngines.Add(SlicerEngineType.SlicerEnginePrusa);
        workerInfo.Capabilities.SupportedFileFormats.AddRange(s_supportedFormats);
        workerInfo.ActiveJobIds.AddRange(new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() });

        // Assert
        Assert.NotNull(workerInfo);
        Assert.Equal("worker-01", workerInfo.WorkerId);
        Assert.Equal(WorkerStatus.Busy, workerInfo.Status);
        Assert.Equal(2, workerInfo.SupportedEngines.Count);
        Assert.Equal(3, workerInfo.Capabilities.SupportedFileFormats.Count);
        Assert.Equal(2, workerInfo.ActiveJobIds.Count);
        Assert.NotNull(workerInfo.Capabilities);
        Assert.NotNull(workerInfo.Metrics);

        // Test serialization
        var bytes = workerInfo.ToByteArray();
        Assert.NotEmpty(bytes);

        var deserialized = WorkerInfo.Parser.ParseFrom(bytes);
        Assert.Equal(workerInfo.WorkerId, deserialized.WorkerId);
        Assert.Equal(workerInfo.Status, deserialized.Status);
        Assert.Equal(workerInfo.SupportedEngines.Count, deserialized.SupportedEngines.Count);
    }

    [Fact]
    public void AllJobStatusEnumValues_AreValid()
    {
        // Act & Assert - Ensure all enum values are defined
        Assert.True(Enum.IsDefined(JobStatus.Unknown));
        Assert.True(Enum.IsDefined(JobStatus.Queued));
        Assert.True(Enum.IsDefined(JobStatus.Slicing));
        Assert.True(Enum.IsDefined(JobStatus.Completed));
        Assert.True(Enum.IsDefined(JobStatus.Error));
        Assert.True(Enum.IsDefined(JobStatus.Cancelled));
    }

    [Fact]
    public void AllSlicerEngineTypeEnumValues_AreValid()
    {
        // Act & Assert - Ensure all enum values are defined
        Assert.True(Enum.IsDefined(SlicerEngineType.SlicerEngineUnknown));
        Assert.True(Enum.IsDefined(SlicerEngineType.SlicerEngineOrca));
        Assert.True(Enum.IsDefined(SlicerEngineType.SlicerEnginePrusa));
        Assert.True(Enum.IsDefined(SlicerEngineType.SlicerEngineSuper));
        Assert.True(Enum.IsDefined(SlicerEngineType.SlicerEngineCura));
    }
}
