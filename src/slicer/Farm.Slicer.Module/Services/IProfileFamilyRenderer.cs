using Farm.Slicer.Module.Dtos;

namespace Farm.Slicer.Module.Services;

/// <summary>
/// Converts authoritative profile-family state into a native OrcaSlicer bundle.
/// </summary>
public interface IProfileFamilyRenderer
{
    /// <summary>Renders a family from resolved worker catalog profiles.</summary>
    ProfileFamilyRenderResult Render(
        Guid familyId,
        CloneProfileFamilyRequestDto request,
        AllProfilesResponseDto sourceCatalog);
}
