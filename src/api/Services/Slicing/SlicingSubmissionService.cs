using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Controllers.Slicing;
using Farm.Web.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Service for handling slicing job submissions
/// </summary>
public class SlicingSubmissionService : ISlicingSubmissionService
{
    private readonly IModelRepository _modelRepository;
    private readonly ISlicerFileStorage _fileStorage;
    private readonly ISlicerOrchestrator _orchestrator;
    private readonly IHostEnvironment _env;
    private readonly IUnifiedLoggingService _logger;

    public SlicingSubmissionService(
        IModelRepository modelRepository,
        ISlicerFileStorage fileStorage,
        ISlicerOrchestrator orchestrator,
        IHostEnvironment env,
        IUnifiedLoggingService logger)
    {
        _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SlicingSubmissionResult> SubmitSlicingJobAsync(
        IFormFile modelFile,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            // Upload file to storage
            string fileKey = $"models/{Guid.NewGuid()}/{modelFile.FileName}";
            string modelFileUrl;
            await using (Stream stream = modelFile.OpenReadStream())
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, stream, "application/octet-stream");
            }

            // Submit job to orchestrator
            SlicingJobRequest request = new()
            {
                UserId = userId,
                PrinterId = printerId,
                ModelFileUrl = new Uri(modelFileUrl, UriKind.RelativeOrAbsolute),
                ModelFileName = modelFile.FileName,
                SlicerEngine = Enum.Parse<SlicerEngineType>(slicerEngine, true),
                SlicerProfile = profile
            };

            SlicingJobResponse response = await _orchestrator.SubmitJobAsync(request);

            // Build result DTO
            SliceResultDto sliceResult = new()
            {
                JobId = response.JobId.ToString(),
                Status = response.Status.ToString(),
                Progress = 0,
                PrintTime = 0,
                FilamentUsed = 0,
                LayerCount = 0,
                GcodeUrl = string.Empty,
                Metadata = new SliceMetadataDto
                {
                    SlicerVersion = string.Equals(slicerEngine, "prusaslicer", StringComparison.OrdinalIgnoreCase)
                        ? "PrusaSlicer 2.7.0"
                        : "OrcaSlicer 1.8.0",
                    ProfileUsed = (profile?.ProcessProfile?.Quality ?? "Unknown") + " - " + (profile?.FilamentProfile?.Material ?? "Unknown"),
                    EstimatedCost = 0
                }
            };

            // In Testing environment register the job in the in-memory SlicingJobStore
            if (_env.IsEnvironment("Testing"))
            {
                string jobId = response.JobId.ToString();
                sliceResult.GcodeUrl = $"/api/slicer/jobs/{jobId}/gcode";
                sliceResult.Status = SlicingJobStatus.Queued.ToString();
                sliceResult.Progress = 0;

                SlicingJobDto storeJob = new()
                {
                    JobId = jobId,
                    Status = SlicingJobStatus.Queued,
                    Progress = 0,
                    SlicerEngine = slicerEngine,
                    PrinterId = printerId,
                    ModelFilePath = modelFileUrl,
                    GcodeFilePath = null,
                    Profile = profile,
                    CreatedAt = DateTime.UtcNow
                };

                _ = SlicingJobStore.Add(storeJob);
            }

            return new SlicingSubmissionResult(true, Result: sliceResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to submit slicing job: {ex.Message}");
            return new SlicingSubmissionResult(false, Error: "Failed to start slicing job");
        }
    }

    public async Task<SlicingSubmissionResult> SubmitSlicingJobFromModelAsync(
        Guid modelId,
        string slicerEngine,
        Guid printerId,
        SlicerProfileDto profile,
        Guid userId,
        CancellationToken ct)
    {
        try
        {
            // Get model from repository
            Model3D? model = await _modelRepository.GetByIdAsync(modelId, ct);
            if (model == null)
            {
                return new SlicingSubmissionResult(false, Error: $"Model with ID {modelId} not found");
            }

            // Validate that the model file exists on disk
            if (!File.Exists(model.FilePath))
            {
                _logger.LogError($"Model file not found on disk: {model.FilePath} for model {modelId}");
                return new SlicingSubmissionResult(false, Error: "Model file not found on disk");
            }

            // Upload the model file to the slicer storage
            string fileKey = $"models/{Guid.NewGuid()}/{model.OriginalFileName}";
            string modelFileUrl;
            using (FileStream fileStream = new(model.FilePath, FileMode.Open, FileAccess.Read))
            {
                modelFileUrl = await _fileStorage.UploadFileAsync(fileKey, fileStream, "application/octet-stream");
            }

            // Submit job to orchestrator
            SlicingJobRequest request = new()
            {
                UserId = userId,
                PrinterId = printerId,
                ModelFileUrl = new Uri(modelFileUrl, UriKind.RelativeOrAbsolute),
                ModelFileName = model.OriginalFileName,
                SlicerEngine = Enum.Parse<SlicerEngineType>(slicerEngine, true),
                SlicerProfile = profile
            };

            SlicingJobResponse response = await _orchestrator.SubmitJobAsync(request);

            // Build result DTO
            SliceResultDto sliceResult = new()
            {
                JobId = response.JobId.ToString(),
                Status = response.Status.ToString(),
                Progress = 0,
                PrintTime = 0,
                FilamentUsed = 0,
                LayerCount = 0,
                GcodeUrl = string.Empty,
                Metadata = new SliceMetadataDto
                {
                    SlicerVersion = string.Equals(slicerEngine, "prusaslicer", StringComparison.OrdinalIgnoreCase)
                        ? "PrusaSlicer 2.7.0"
                        : "OrcaSlicer 1.8.0",
                    ProfileUsed = (profile?.ProcessProfile?.Quality ?? "Unknown") + " - " + (profile?.FilamentProfile?.Material ?? "Unknown"),
                    EstimatedCost = 0
                }
            };

            // In Testing environment register the job in the in-memory SlicingJobStore
            if (_env.IsEnvironment("Testing"))
            {
                string jobId = response.JobId.ToString();
                sliceResult.GcodeUrl = $"/api/slicer/jobs/{jobId}/gcode";
                sliceResult.Status = SlicingJobStatus.Queued.ToString();
                sliceResult.Progress = 0;

                SlicingJobDto storeJob = new()
                {
                    JobId = jobId,
                    Status = SlicingJobStatus.Queued,
                    Progress = 0,
                    SlicerEngine = slicerEngine,
                    PrinterId = printerId,
                    ModelFilePath = modelFileUrl,
                    GcodeFilePath = null,
                    Profile = profile,
                    CreatedAt = DateTime.UtcNow
                };

                _ = SlicingJobStore.Add(storeJob);
            }

            _logger.LogInformation($"Slicing job submitted for uploaded model {modelId} ({model.OriginalFileName})");

            return new SlicingSubmissionResult(true, Result: sliceResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to submit slicing job for model {modelId}: {ex.Message}");
            return new SlicingSubmissionResult(false, Error: "Failed to start slicing job");
        }
    }
}
