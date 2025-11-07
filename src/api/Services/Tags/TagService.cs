using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Telemetry;
using Farm.Web.Api.Repositories.Model;
using Farm.Web.Api.Repositories.Tags;
using Farm.Web.Shared;

namespace Farm.Web.Api.Services.Tags
{
    public class TagService : ITagService
    {
        private readonly ITagRepository _tagRepository;
        private readonly IModelTagMappingRepository _mappingRepository;
        private readonly IModelRepository _modelRepository;
        private readonly IUnifiedLoggingService _logger;

        public TagService(
            ITagRepository tagRepository,
            IModelTagMappingRepository mappingRepository,
            IModelRepository modelRepository,
            IUnifiedLoggingService logger)
        {
            _tagRepository = tagRepository ?? throw new ArgumentNullException(nameof(tagRepository));
            _mappingRepository = mappingRepository ?? throw new ArgumentNullException(nameof(mappingRepository));
            _modelRepository = modelRepository ?? throw new ArgumentNullException(nameof(modelRepository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<IReadOnlyList<Model3DTagDto>> GetAllTagsAsync(CancellationToken ct)
        {
            try
            {
                var tags = await _tagRepository.ListAllAsync(ct);
                return tags.Select(t => new Model3DTagDto
                {
                    Id = t.Id,
                    Name = t.Name,
                    Color = t.Color,
                    Description = t.Description
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get all tags: {ex.Message}");
                throw;
            }
        }

        public async Task<Model3DTagDto?> GetTagByIdAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                var tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    return null;
                }

                return new Model3DTagDto
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Color = tag.Color,
                    Description = tag.Description
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tag {tagId}: {ex.Message}");
                throw;
            }
        }

        public async Task<Model3DTagDto> CreateTagAsync(CreateModel3DTagDto dto, CancellationToken ct)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(dto);

                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    throw new ArgumentException("Tag name is required", nameof(dto.Name));
                }

                // Check if tag already exists
                var existing = await _tagRepository.GetByNameAsync(dto.Name, ct);
                if (existing != null)
                {
                    throw new InvalidOperationException($"Tag '{dto.Name}' already exists");
                }

                var tag = new Model3DTag
                {
                    Id = Guid.NewGuid(),
                    Name = dto.Name.Trim(),
                    Color = dto.Color,
                    Description = dto.Description,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _tagRepository.AddAsync(tag, ct);
                await _tagRepository.SaveChangesAsync(ct);

                return new Model3DTagDto
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Color = tag.Color,
                    Description = tag.Description
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to create tag: {ex.Message}");
                throw;
            }
        }

        public async Task DeleteTagAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                var tag = await _tagRepository.GetByIdAsync(tagId, ct);
                if (tag == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not found");
                }

                // Tag deletion cascade handled by EF Core configuration
                await _tagRepository.RemoveAsync(tag, ct);
                await _tagRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to delete tag {tagId}: {ex.Message}");
                throw;
            }
        }

        public async Task AssignTagsToModelAsync(Guid modelId, IEnumerable<Guid> tagIds, CancellationToken ct)
        {
            try
            {
                var tagIdList = tagIds?.ToList() ?? new List<Guid>();

                // Verify model exists
                var model = await _modelRepository.GetByIdAsync(modelId, ct);
                if (model == null)
                {
                    throw new KeyNotFoundException($"Model {modelId} not found");
                }

                // Remove existing tag mappings
                await _mappingRepository.RemoveByModelIdAsync(modelId, ct);

                // Add new tag mappings
                foreach (var tagId in tagIdList)
                {
                    var tag = await _tagRepository.GetByIdAsync(tagId, ct);
                    if (tag != null)
                    {
                        var mapping = new Model3DTagMapping
                        {
                            Id = Guid.NewGuid(),
                            Model3DId = modelId,
                            TagId = tagId,
                            TaggedAt = DateTime.UtcNow
                        };
                        await _mappingRepository.AddAsync(mapping, ct);
                    }
                }

                await _mappingRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to assign tags to model {modelId}: {ex.Message}");
                throw;
            }
        }

        public async Task RemoveTagFromModelAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            try
            {
                var mapping = await _mappingRepository.GetMappingAsync(modelId, tagId, ct);
                if (mapping == null)
                {
                    throw new KeyNotFoundException($"Tag {tagId} not assigned to model {modelId}");
                }

                await _mappingRepository.RemoveAsync(mapping, ct);
                await _mappingRepository.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to remove tag {tagId} from model {modelId}: {ex.Message}");
                throw;
            }
        }

        public async Task<IReadOnlyList<Model3DTagDto>> GetModelTagsAsync(Guid modelId, CancellationToken ct)
        {
            try
            {
                var mappings = await _mappingRepository.GetByModelIdAsync(modelId, ct);
                var tagIds = mappings.Select(m => m.TagId).ToList();

                var tags = new List<Model3DTagDto>();
                foreach (var tagId in tagIds)
                {
                    var tag = await _tagRepository.GetByIdAsync(tagId, ct);
                    if (tag != null)
                    {
                        tags.Add(new Model3DTagDto
                        {
                            Id = tag.Id,
                            Name = tag.Name,
                            Color = tag.Color,
                            Description = tag.Description
                        });
                    }
                }

                return tags;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to get tags for model {modelId}: {ex.Message}");
                throw;
            }
        }

        public async Task BulkAssignTagsAsync(IEnumerable<Guid> modelIds, IEnumerable<Guid> tagIds, CancellationToken ct)
        {
            try
            {
                var modelIdList = modelIds?.ToList() ?? new List<Guid>();
                var tagIdList = tagIds?.ToList() ?? new List<Guid>();

                foreach (var modelId in modelIdList)
                {
                    await AssignTagsToModelAsync(modelId, tagIdList, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to bulk assign tags: {ex.Message}");
                throw;
            }
        }
    }
}
