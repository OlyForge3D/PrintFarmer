using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Mapping;

/// <summary>
/// AutoMapper profile for mapping Location entities to DTOs.
/// </summary>
public class LocationMappingProfile : Profile
{
    public LocationMappingProfile()
    {
        // Location entity to LocationDto
        CreateMap<Location, LocationDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(dest => dest.PrinterCount, opt => opt.MapFrom(src => src.Printers.Count))
            .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(dest => dest.ModifiedAt, opt => opt.MapFrom(src => src.ModifiedAt))
            .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsActive));

        // Location entity to LocationDetailsDto
        CreateMap<Location, LocationDetailsDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(dest => dest.PrinterCount, opt => opt.MapFrom(src => src.Printers.Count))
            .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(dest => dest.ModifiedAt, opt => opt.MapFrom(src => src.ModifiedAt))
            .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsActive))
            .ForMember(dest => dest.Printers, opt => opt.MapFrom(src =>
                src.Printers.Select(p => new DiscoveryPrinterInfoDto
                {
                    Name = p.Name,
                    ServerUrl = p.ServerUrl,
                    IpAddress = string.Empty,  // No longer stored on Printer entity
                    Backend = (PrinterBackend)p.Backend,
                    BackendPort = p.BackendPort,
                    FrontendPort = p.FrontendPort,
                    CameraStreamUrl = p.CameraStreamUrl ?? string.Empty
                }).ToArray()));
    }
}
