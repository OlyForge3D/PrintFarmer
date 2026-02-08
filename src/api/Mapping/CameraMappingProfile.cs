using AutoMapper;
using Farm.Infrastructure;
using Camera = Farm.Infrastructure.Domain.Camera;

namespace Farm.Web.Api.Mapping;

/// <summary>
/// AutoMapper profile for mapping Camera entities to DTOs.
/// </summary>
public class CameraMappingProfile : Profile
{
    public CameraMappingProfile()
    {
        // Camera entity to CameraDto
        CreateMap<Camera, CameraDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(dest => dest.StreamUrl, opt => opt.MapFrom(src => src.StreamUrl))
            .ForMember(dest => dest.SnapshotUrl, opt => opt.MapFrom(src => src.SnapshotUrl))
            .ForMember(dest => dest.IsEnabled, opt => opt.MapFrom(src => src.IsEnabled))
            .ForMember(dest => dest.SortOrder, opt => opt.MapFrom(src => src.SortOrder))
            .ForMember(dest => dest.Location, opt => opt.MapFrom(src => src.Location))
            .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(dest => dest.UpdatedAt, opt => opt.MapFrom(src => src.UpdatedAt))
            .ForMember(dest => dest.IsStandalone, opt => opt.MapFrom(src => true));
    }
}
