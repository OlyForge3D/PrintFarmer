using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;

namespace Farm.Web.Api.Services.Model
{
    public interface IModelService
    {
        Task<IReadOnlyList<Model3DDto>> ListModelsAsync(CancellationToken ct);
        Task<Model3DDto?> GetModelAsync(Guid id, CancellationToken ct);
        Task<string?> GetModelFilePathAsync(Guid id, CancellationToken ct);
        Task<string?> GetModelThumbnailPathAsync(Guid id, CancellationToken ct);
        Task DeleteModelAsync(Guid id, CancellationToken ct);
        Model3DValidationResultDto ValidateModel(IFormFile modelFile);
        Task<Model3DUploadResultDto> UploadModelAsync(IFormFile modelFile, CancellationToken ct);
    }
}
