using Farm.Infrastructure;

namespace Farm.Web.Api.DTOs;

public record HierarchicalPrinterModelProfilesDto
{
    public string Name { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public List<MachineProfileListItemDto> MachineProfiles { get; init; } = new();
    public List<FilamentProfileListItemDto> FilamentProfiles { get; init; } = new();
    public List<ProcessProfileListItemDto> ProcessProfiles { get; init; } = new();
}

public record HierarchicalManufacturerProfilesDto
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, HierarchicalPrinterModelProfilesDto> Models { get; init; } = new();
}

public record HierarchicalProfilesResponseDto
{
    public Dictionary<string, HierarchicalManufacturerProfilesDto> ByHierarchy { get; init; } = new();
    public Dictionary<string, List<MachineProfileListItemDto>> MachineProfiles { get; init; } = new();
    public Dictionary<string, List<FilamentProfileListItemDto>> FilamentProfiles { get; init; } = new();
    public Dictionary<string, List<ProcessProfileListItemDto>> ProcessProfiles { get; init; } = new();
}
