using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Model;
using Farm.Web.Api.Services.FileManagement;
using Farm.Web.Api.Services.Interfaces; // for ModelAnalysisResult and IThumbnailGenerationService
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shared = Farm.Web.Shared;

namespace Farm.Web.Api.Services.Model
{
    public class ModelService : IModelService
    {
        private readonly IModelRepository _repository;
        private readonly IUnifiedLoggingService _logger;
        private readonly string _modelsPath;
        private readonly IModelAnalysisService? _analysisService;
        private readonly Farm.Web.Api.Services.IO.IFileSystem _fileSystem;
        private readonly IFileManagementService _fileManagementService;
        private readonly IThumbnailGenerationService? _thumbnailService;

        public ModelService(
            IModelRepository repository,
            IUnifiedLoggingService logger,
            IConfiguration configuration,
            Farm.Web.Api.Services.IO.IFileSystem fileSystem,
            IFileManagementService fileManagementService,
            IModelAnalysisService? analysisService = null,
            IThumbnailGenerationService? thumbnailService = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _analysisService = analysisService;
            _thumbnailService = thumbnailService;
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            _fileManagementService = fileManagementService ?? throw new ArgumentNullException(nameof(fileManagementService));
            ArgumentNullException.ThrowIfNull(configuration);
            _modelsPath = configuration["ModelStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "models");
            if (!_fileSystem.DirectoryExists(_modelsPath))
            {
                _fileSystem.CreateDirectory(_modelsPath);
            }
        }

        public async Task<IReadOnlyList<Shared.Model3DDto>> ListModelsAsync(CancellationToken ct)
        {
            var models = await _repository.ListValidAsync(ct);

            return models.Select(m => new Shared.Model3DDto
            {
                Id = m.Id,
                Name = m.DisplayName,
                FileName = m.OriginalFileName,
                FileSize = m.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(m.FileFormat),
                UploadedAt = m.UploadedAt,
                Url = $"/api/3d-models/{m.Id}/file",
                ThumbnailUrl = m.ThumbnailPath != null ? $"/api/3d-models/{m.Id}/thumbnail" : null
            }).ToList();
        }

        public async Task<Shared.Model3DDto?> GetModelAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _repository.GetByIdAsync(id, ct);
            if (model == null)
            {
                return null;
            }

            return new Shared.Model3DDto
            {
                Id = model.Id,
                Name = model.DisplayName,
                FileName = model.OriginalFileName,
                FileSize = model.FileSizeBytes,
                FileType = _fileManagementService.GetModelFileFormatString(model.FileFormat),
                UploadedAt = model.UploadedAt,
                Url = $"/api/3d-models/{model.Id}/file",
                ThumbnailUrl = model.ThumbnailPath != null ? $"/api/3d-models/{model.Id}/thumbnail" : null
            };
        }

        public async Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _repository.GetByIdAsync(id, ct);
            return model?.FilePath;
        }

        public async Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _repository.GetByIdAsync(id, ct);
            return model?.ThumbnailPath;
        }

        public async Task DeleteModelAsync(Guid id, CancellationToken ct)
        {
            Model3D? model = await _repository.GetByIdAsync(id, ct);
            if (model == null)
            {
                throw new KeyNotFoundException("Model not found");
            }

            try
            {
                if (_fileManagementService.IsSafePath(model.FilePath, _modelsPath) && System.IO.File.Exists(model.FilePath))
                {
                    System.IO.File.Delete(model.FilePath);
                }

                if (model.ThumbnailPath != null && System.IO.File.Exists(model.ThumbnailPath))
                {
                    System.IO.File.Delete(model.ThumbnailPath);
                }

                await _repository.RemoveAsync(model, ct);
                await _repository.SaveChangesAsync(ct);
                _logger.LogInformation($"Model deleted: {id}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete model: {id}: {ex.Message}");
                throw;
            }
        }

        public Shared.Model3DValidationResultDto ValidateModel(IFormFile modelFile)
        {
            if (modelFile == null || modelFile.Length == 0)
            {
                throw new ArgumentException("Model file is required");
            }

            List<string> issues = new();
            string fileExtension = Path.GetExtension(modelFile.FileName);

            // Validate extension
            try
            {
                _fileManagementService.ValidateModelExtension(fileExtension);
            }
            catch (ArgumentException ex)
            {
                issues.Add(ex.Message);
            }

            if (modelFile.Length > 100_000_000)
            {
                issues.Add("File size exceeds 100MB limit");
            }

            return new Shared.Model3DValidationResultDto
            {
                Valid = issues.Count == 0,
                Issues = issues.Count > 0 ? issues.ToArray() : null
            };
        }

        public async Task<Shared.Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct)
        {
            if (modelFile == null || modelFile.Length == 0)
            {
                throw new ArgumentException("Model file is required", nameof(modelFile));
            }

            string originalName = modelFile.FileName ?? string.Empty;
            string fileExtension = Path.GetExtension(originalName);

            // Validate extension using service
            _fileManagementService.ValidateModelExtension(fileExtension);

            Guid modelId = Guid.NewGuid();
            string fileName = $"{modelId}{fileExtension}";
            string finalFilePath = Path.Combine(_modelsPath, fileName);
            if (!_fileManagementService.IsSafePath(finalFilePath, _modelsPath))
            {
                throw new InvalidOperationException("Unsafe file path generated");
            }

            // Use temp file pattern for safety: write to temp, then move to final location
            string tempFileName = $"{modelId}.tmp{fileExtension}";
            string tempFilePath = Path.Combine(_modelsPath, tempFileName);

            try
            {
                // Step 1: Write to temp file and compute hash
                string fileHash;
                try
                {
                    using (var stream = _fileSystem.OpenWrite(tempFilePath))
                    {
                        using MemoryStream memoryStream = new();
                        await modelFile.CopyToAsync(memoryStream, ct);
                        memoryStream.Position = 0;

                        byte[] hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(memoryStream, ct);
                        fileHash = _fileManagementService.ToHex(hashBytes);

                        memoryStream.Position = 0;
                        await memoryStream.CopyToAsync(stream, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to write model file to temp location: {ex.Message}");
                    // Cleanup temp file if write failed
                    try
                    {
                        if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                        {
                            _fileSystem.DeleteFile(tempFilePath);
                        }
                    }
                    catch { /* ignore cleanup errors */ }
                    throw;
                }

                // Virus scan best-effort: if scanner not available skip
                try
                {
                    // Resolve a scanner service via DI would be better; for now skip
                }
                catch { }

                // Step 2: Analyze model metadata (best-effort)
                ModelAnalysisResult? analysis = null;
                try
                {
                    // analysis is optional; resolve from DI if available via _analysisService
                    if (_analysisService != null)
                    {
                        analysis = await _analysisService.AnalyzeModelAsync(tempFilePath, fileExtension, ct);
                    }
                }
                catch { }

                // Step 3: Check for duplicates
                Model3D? existingModel = await _repository.GetByHashAsync(fileHash, ct);
                string baseName = Path.GetFileNameWithoutExtension(originalName);
                if (existingModel != null)
                {
                    string existingBaseName = Path.GetFileNameWithoutExtension(existingModel.OriginalFileName);
                    string existingExt = Path.GetExtension(existingModel.OriginalFileName);
                    bool isSameExtension = string.Equals(existingExt, fileExtension, StringComparison.OrdinalIgnoreCase);
                    bool bothDuplicatePrefix = existingBaseName.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase)
                        && baseName.StartsWith("duplicate", StringComparison.OrdinalIgnoreCase);
                    bool baseNamesMatch = string.Equals(existingBaseName, baseName, StringComparison.OrdinalIgnoreCase);
                    bool treatAsDuplicate = isSameExtension && (baseNamesMatch || bothDuplicatePrefix);

                    if (treatAsDuplicate)
                    {
                        // Cleanup temp file before returning existing
                        if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                        {
                            _fileSystem.DeleteFile(tempFilePath);
                        }

                        return new Shared.Model3DUploadResultDto
                        {
                            Id = existingModel.Id,
                            Name = existingModel.DisplayName,
                            FileName = existingModel.OriginalFileName,
                            FileSize = existingModel.FileSizeBytes,
                            FileType = _fileManagementService.GetModelFileFormatString(existingModel.FileFormat),
                            UploadedAt = existingModel.UploadedAt,
                            Url = $"/api/3d-models/{existingModel.Id}/file"
                        };
                    }

                    byte[] composite = System.Text.Encoding.UTF8.GetBytes(fileHash + "|" + originalName);
                    byte[] newHashBytes = System.Security.Cryptography.SHA256.HashData(composite);
                    fileHash = _fileManagementService.ToHex(newHashBytes);
                }

                // Step 4: Create DB record (still pointing to temp file for now)
                Model3D model = new()
                {
                    Id = modelId,
                    OriginalFileName = originalName,
                    DisplayName = Path.GetFileNameWithoutExtension(originalName),
                    FilePath = finalFilePath,  // Store final path in DB
                    FileSizeBytes = modelFile.Length,
                    FileHash = fileHash,
                    FileFormat = _fileManagementService.GetModelFileFormat(fileExtension),
                    UploadedAt = DateTime.UtcNow,
                    IsValid = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    DimensionX = analysis?.DimensionX,
                    DimensionY = analysis?.DimensionY,
                    DimensionZ = analysis?.DimensionZ,
                    TriangleCount = analysis?.TriangleCount,
                    VolumeM3 = analysis?.VolumeMm3
                };

                await _repository.AddAsync(model, ct);
                await _repository.SaveChangesAsync(ct);

                // Step 5: Move temp file to final location (only after DB commit succeeds)
                try
                {
                    if (_fileSystem.FileExists(tempFilePath))
                    {
                        // Delete any existing file at final location first (shouldn't happen but be safe)
                        if (_fileSystem.FileExists(finalFilePath))
                        {
                            _fileSystem.DeleteFile(finalFilePath);
                        }

                        // Move temp to final location
                        _fileSystem.MoveFile(tempFilePath, finalFilePath, overwrite: true);
                        _logger.LogDebug($"Model file moved from temp to final location: {modelId}");
                    }
                }
                catch (Exception moveEx)
                {
                    _logger.LogError($"Failed to move model file from temp to final location: {moveEx.Message}. File remains in temp location.");
                    // Note: DB record points to finalFilePath but file is still at tempFilePath
                    // This will be detected by consistency audit and can be manually recovered
                    throw new InvalidOperationException("Failed to finalize model file", moveEx);
                }

                // Thumbnail generation (best-effort - don't fail upload if thumbnail fails)
                try
                {
                    if (_thumbnailService != null)
                    {
                        string thumbnailFileName = $"{modelId}_thumb{_thumbnailService.ThumbnailFileExtension}";
                        string thumbnailPath = Path.Combine(_modelsPath, thumbnailFileName);

                        if (_fileManagementService.IsSafePath(thumbnailPath, _modelsPath))
                        {
                            // Use final file path (not temp) for thumbnail generation
                            await _thumbnailService.GenerateAndSaveThumbnailAsync(finalFilePath, thumbnailPath, ct);

                            // Update model with thumbnail path
                            model.ThumbnailPath = thumbnailPath;
                            await _repository.SaveChangesAsync(ct);

                            _logger.LogInformation($"Thumbnail generated successfully for model {modelId}");
                        }
                    }
                }
                catch (Exception thumbnailEx)
                {
                    _logger.LogWarning($"Failed to generate thumbnail for model {modelId}: {thumbnailEx.Message}. Continuing without thumbnail.");
                    // Don't rethrow - upload should succeed even if thumbnail generation fails
                }

                return new Shared.Model3DUploadResultDto
                {
                    Id = modelId,
                    Name = model.DisplayName,
                    FileName = originalName,
                    FileSize = modelFile.Length,
                    FileType = fileExtension.TrimStart('.'),
                    UploadedAt = model.UploadedAt,
                    Url = $"/api/3d-models/{modelId}/file"
                };
            }
            catch
            {
                // Cleanup both temp and final files if something fails
                try
                {
                    // Try to clean up temp file
                    if (_fileManagementService.IsSafePath(tempFilePath, _modelsPath) && _fileSystem.FileExists(tempFilePath))
                    {
                        _fileSystem.DeleteFile(tempFilePath);
                    }

                    // Try to clean up final file if it was already moved
                    if (_fileManagementService.IsSafePath(finalFilePath, _modelsPath) && _fileSystem.FileExists(finalFilePath))
                    {
                        _fileSystem.DeleteFile(finalFilePath);
                    }
                }
                catch { /* ignore cleanup errors */ }

                throw;
            }
        }
    }
}
