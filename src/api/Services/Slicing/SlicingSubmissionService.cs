using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.UnitOfWork;
using Farm.Infrastructure.Telemetry;
using Farm.Slicer.Module.Api.Controllers.Slicing;
using Farm.Slicer.Module.Data.Repositories;
using Farm.Slicer.Module.Domain;
using Farm.Slicer.Module.Services;
using Farm.Web.Api.Services.FileManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using IStoredFileOperationsService = Farm.Web.Api.Services.FileManagement.IStoredFileOperationsService;

namespace Farm.Web.Api.Services.Slicing;

/// <summary>
/// Service for handling slicing job submissions
/// </summary>
public class SlicingSubmissionService(
    IUnitOfWork unitOfWork,
    IModel3DFileRepository model3dFiles,
    ISlicerFileStorage fileStorage,
    ISlicerOrchestrator orchestrator,
    IHostEnvironment env,
    IUnifiedLoggingService logger,
    IStoredFileOperationsService fileOperations) : Farm.Slicer.Module.Services.ISlicingSubmissionService
{
    private readonly IModel3DFileRepository _model3dFiles = model3dFiles ?? throw new ArgumentNullException(nameof(model3dFiles));
    private readonly ISlicerFileStorage _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
    private readonly ISlicerOrchestrator _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    private readonly IHostEnvironment _env = env ?? throw new ArgumentNullException(nameof(env));
    private readonly IUnifiedLoggingService _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IStoredFileOperationsService _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));

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
                SlicerEngine = Enum.Parse<SlicerEngineType>(slicerEngine, true)
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
                sliceResult.GcodeUrl = _fileOperations.BuildSlicerJobGcodeUrl(response.JobId);
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
                    CreatedAt = DateTime.UtcNow,
                    Profile = profile
                };

                SlicingJobStore.AddOrUpdate(response.JobId, storeJob);
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
            Model3D? model = await _model3dFiles.GetByIdAsync(modelId, ct);
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
            string fileKey = $"models/{Guid.NewGuid()}/{model.FileName}";
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
                ModelFileName = model.FileName,
                SlicerEngine = Enum.Parse<SlicerEngineType>(slicerEngine, true)
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
                sliceResult.GcodeUrl = _fileOperations.BuildSlicerJobGcodeUrl(response.JobId);
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
                    CreatedAt = DateTime.UtcNow,
                    Profile = profile
                };

                SlicingJobStore.AddOrUpdate(response.JobId, storeJob);
            }

            _logger.LogInformation($"Slicing job submitted for uploaded model {modelId} ({model.FileName})");

            return new SlicingSubmissionResult(true, Result: sliceResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Failed to submit slicing job for model {modelId}: {ex.Message}");
            return new SlicingSubmissionResult(false, Error: "Failed to start slicing job");
        }
    }
}
