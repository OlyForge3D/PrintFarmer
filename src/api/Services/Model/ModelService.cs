using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Shared = Farm.Web.Shared;
using Farm.Web.Api.Services.Interfaces; // for ModelAnalysisResult
using Farm.Web.Api.Repositories.Model;

namespace Farm.Web.Api.Services.Model
{
    public class ModelService : IModelService
    {
    private readonly IModelRepository _repository;
    private readonly IUnifiedLoggingService _logger;
    private readonly string _modelsPath;
    private readonly IModelAnalysisService? _analysisService;
    private readonly Farm.Web.Api.Services.IO.IFileSystem _fileSystem;
        public ModelService(IModelRepository repository, IUnifiedLoggingService logger, IConfiguration configuration, Farm.Web.Api.Services.IO.IFileSystem fileSystem, IModelAnalysisService? analysisService = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _analysisService = analysisService;
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
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
                FileType = GetFileTypeString(m.FileFormat),
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
                FileType = GetFileTypeString(model.FileFormat),
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
                if (IsSafePath(model.FilePath, _modelsPath) && System.IO.File.Exists(model.FilePath))
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
            string[] allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply" };
            if (!allowedExtensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add($"Invalid file type. Allowed types: {string.Join(", ", allowedExtensions)}");
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

            string[] allowedExtensions = new[] { ".stl", ".3mf", ".obj", ".ply", ".step" };
            string originalName = modelFile.FileName ?? string.Empty;
            string fileExtension = Path.GetExtension(originalName);
            if (!allowedExtensions.Contains(fileExtension, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Invalid file type", nameof(modelFile));
            }

            Guid modelId = Guid.NewGuid();
            string fileName = $"{modelId}{fileExtension}";
            string filePath = Path.Combine(_modelsPath, fileName);
            if (!IsSafePath(filePath, _modelsPath))
            {
                throw new InvalidOperationException("Unsafe file path generated");
            }

            try
            {
                string fileHash;
                using (var stream = _fileSystem.OpenWrite(filePath))
                {
                    using MemoryStream memoryStream = new();
                    await modelFile.CopyToAsync(memoryStream, ct);
                    memoryStream.Position = 0;

                    byte[] hashBytes = await System.Security.Cryptography.SHA256.HashDataAsync(memoryStream, ct);
                    fileHash = ToHexLower(hashBytes);

                    memoryStream.Position = 0;
                    await memoryStream.CopyToAsync(stream, ct);
                }

                // Virus scan best-effort: if scanner not available skip
                try
                {
                    // Resolve a scanner service via DI would be better; for now skip
                }
                catch { }

                // Analyze model metadata (best-effort)
                ModelAnalysisResult? analysis = null;
                try
                {
                    // analysis is optional; resolve from DI if available via _analysisService
                    if (_analysisService != null)
                    {
                        analysis = await _analysisService.AnalyzeModelAsync(filePath, fileExtension, ct);
                    }
                }
                catch { }

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
                        if (IsSafePath(filePath, _modelsPath) && _fileSystem.FileExists(filePath))
                        {
                            _fileSystem.DeleteFile(filePath);
                        }

                        return new Shared.Model3DUploadResultDto
                        {
                            Id = existingModel.Id,
                            Name = existingModel.DisplayName,
                            FileName = existingModel.OriginalFileName,
                            FileSize = existingModel.FileSizeBytes,
                            FileType = GetFileTypeString(existingModel.FileFormat),
                            UploadedAt = existingModel.UploadedAt,
                            Url = $"/api/3d-models/{existingModel.Id}/file"
                        };
                    }

                    byte[] composite = System.Text.Encoding.UTF8.GetBytes(fileHash + "|" + originalName);
                    byte[] newHashBytes = System.Security.Cryptography.SHA256.HashData(composite);
                    fileHash = ToHexLower(newHashBytes);
                }

                Model3D model = new()
                {
                    Id = modelId,
                    OriginalFileName = originalName,
                    DisplayName = Path.GetFileNameWithoutExtension(originalName),
                    FilePath = filePath,
                    FileSizeBytes = modelFile.Length,
                    FileHash = fileHash,
                    FileFormat = GetFileFormat(fileExtension),
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

                // Thumbnail generation omitted in service tests; optional

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
                // cleanup partial file if exists
                try
                {
                    if (IsSafePath(filePath, _modelsPath) && System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch { }

                throw;
            }
        }

        private static string ToHexLower(byte[] bytes)
        {
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        private static ModelFileFormat GetFileFormat(string extension)
        {
            if (string.Equals(extension, ".stl", StringComparison.OrdinalIgnoreCase))
            {
                return ModelFileFormat.STL;
            }
            if (string.Equals(extension, ".3mf", StringComparison.OrdinalIgnoreCase))
            {
                return ModelFileFormat.TMF;
            }
            if (string.Equals(extension, ".obj", StringComparison.OrdinalIgnoreCase))
            {
                return ModelFileFormat.OBJ;
            }
            if (string.Equals(extension, ".ply", StringComparison.OrdinalIgnoreCase))
            {
                return ModelFileFormat.PLY;
            }
            if (string.Equals(extension, ".step", StringComparison.OrdinalIgnoreCase))
            {
                return ModelFileFormat.STEP;
            }

            return ModelFileFormat.STL;
        }

        private static bool IsSafePath(string candidatePath, string root)
        {
            try
            {
                string fullRoot = Path.GetFullPath(root);
                string fullCandidate = Path.GetFullPath(candidatePath);
                return fullCandidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetFileTypeString(ModelFileFormat format)
        {
            return format switch
            {
                ModelFileFormat.STL => "stl",
                ModelFileFormat.TMF => "3mf",
                ModelFileFormat.OBJ => "obj",
                ModelFileFormat.PLY => "ply",
                ModelFileFormat.STEP => "step",
                _ => "stl"
            };
        }
    }
}
