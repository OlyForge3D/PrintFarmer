using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Creates and installs farm-wide custom OrcaSlicer profile families.
/// </summary>
public interface IProfileFamilyService
{
    /// <summary>Creates one family and all selected nozzle variants.</summary>
    Task<CloneProfileFamilyResponseDto> CloneFamilyAsync(
        CloneProfileFamilyRequestDto request,
        Guid userId,
        CancellationToken ct);
}
