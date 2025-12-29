using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;
using Farm.Infrastructure.Repositories.Model;
using Farm.Infrastructure.Repositories.Tags;
using Farm.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace Farm.Web.Api.Services.Tags
{
    public class TagService : ITagService
    {
        private readonly ITagRepository _tagRepository;
        private readonly IModelTagMappingRepository _mappingRepository;
        private readonly IModel3dFileRepository _modelRepository;
        private readonly IUnifiedLoggingService _logger;

        public TagService(
            ITagRepository tagRepository,
            IModelTagMappingRepository mappingRepository,
            IModel3dFileRepository modelRepository,
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
                IReadOnlyList<Model3DTag> tags = await _tagRepository.ListAllAsync(ct);
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
                Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
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
                    throw new ArgumentException("Tag name is required", nameof(dto));
                }

                string trimmedName = dto.Name.Trim();
                string normalizedName = ToPascalCase(trimmedName);

                // Check if tag already exists (after normalization)
                Model3DTag? existing = await _tagRepository.GetByNameAsync(normalizedName, ct);
                if (existing != null)
                {
                    // Return the existing tag
                    return new Model3DTagDto
                    {
                        Id = existing.Id,
                        Name = existing.Name,
                        Color = existing.Color,
                        Description = existing.Description
                    };
                }

                Model3DTag tag = new Model3DTag
                {
                    Id = Guid.NewGuid(),
                    Name = normalizedName,
                    Color = dto.Color,
                    Description = dto.Description,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };

                await _tagRepository.AddAsync(tag, ct);
                try
                {
                    await _tagRepository.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true
                    || ex.InnerException?.Message.Contains("Violation of PRIMARY KEY") == true
                    || ex.InnerException?.Message.Contains("duplicate key") == true)
                {
                    // Handle race condition: tag was created between check and insert
                    // Fetch and return the existing tag
                    Model3DTag? existingTag = await _tagRepository.GetByNameAsync(normalizedName, ct);
                    if (existingTag != null)
                    {
                        return new Model3DTagDto
                        {
                            Id = existingTag.Id,
                            Name = existingTag.Name,
                            Color = existingTag.Color,
                            Description = existingTag.Description
                        };
                    }
                    throw;
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
                _logger.LogError($"Failed to create tag: {ex.Message}\n{ex.StackTrace}");
                throw;
            }
        }

        /// <summary>
        /// Convert string to PascalCase (capitalize first letter of each word)
        /// </summary>
        private static string ToPascalCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            // First, convert to lowercase to normalize
            string lowered = input.ToLowerInvariant();

            string[] words = lowered.Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            // Handle case where input was only delimiters
            if (words.Length == 0)
            {
                return input;
            }

            IEnumerable<string> pascalWords = words.Select(word =>
            {
                // Safety check in case word is somehow empty
                if (string.IsNullOrEmpty(word))
                {
                    return "";
                }

                return char.ToUpperInvariant(word[0]) + (word.Length > 1 ? word.Substring(1) : "");
            });

            return string.Concat(pascalWords);
        }

        public async Task DeleteTagAsync(Guid tagId, CancellationToken ct)
        {
            try
            {
                Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
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
                List<Guid> tagIdList = tagIds?.ToList() ?? new List<Guid>();
                _logger.LogInformation($"Assigning {tagIdList.Count} tags to model {modelId}");

                // Verify model exists
                Model3D? model = await _modelRepository.GetByIdAsync(modelId, ct);
                if (model == null)
                {
                    _logger.LogError($"Model {modelId} not found");
                    throw new KeyNotFoundException($"Model {modelId} not found");
                }
                _logger.LogInformation($"Model {modelId} found, proceeding with tag assignment");

                // Remove existing tag mappings
                _logger.LogInformation($"Removing existing tag mappings for model {modelId}");
                await _mappingRepository.RemoveByModelIdAsync(modelId, ct);

                // Add new tag mappings
                _logger.LogInformation($"Adding {tagIdList.Count} new tag mappings");
                foreach (Guid tagId in tagIdList)
                {
                    Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
                    if (tag != null)
                    {
                        Model3DTagMapping mapping = new Model3DTagMapping
                        {
                            Id = Guid.NewGuid(),
                            Model3DId = modelId,
                            TagId = tagId,
                            TaggedAt = DateTime.UtcNow
                        };
                        _logger.LogInformation($"Adding tag mapping: Model={modelId}, Tag={tagId}");
                        await _mappingRepository.AddAsync(mapping, ct);
                    }
                    else
                    {
                        _logger.LogWarning($"Tag {tagId} not found");
                    }
                }

                _logger.LogInformation($"Saving changes to database");
                await _mappingRepository.SaveChangesAsync(ct);
                _logger.LogInformation($"Successfully assigned tags to model {modelId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to assign tags to model {modelId}: {ex.GetType().Name} - {ex.Message}");
                _logger.LogError($"Stack trace: {ex.StackTrace}");
                throw;
            }
        }

        public async Task RemoveTagFromModelAsync(Guid modelId, Guid tagId, CancellationToken ct)
        {
            try
            {
                Model3DTagMapping? mapping = await _mappingRepository.GetMappingAsync(modelId, tagId, ct);
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
                IReadOnlyList<Model3DTagMapping> mappings = await _mappingRepository.GetByModelIdAsync(modelId, ct);
                List<Guid> tagIds = mappings.Select(m => m.TagId).ToList();

                List<Model3DTagDto> tags = new List<Model3DTagDto>();
                foreach (Guid tagId in tagIds)
                {
                    Model3DTag? tag = await _tagRepository.GetByIdAsync(tagId, ct);
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
                List<Guid> modelIdList = modelIds?.ToList() ?? new List<Guid>();
                List<Guid> tagIdList = tagIds?.ToList() ?? new List<Guid>();

                foreach (Guid modelId in modelIdList)
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
