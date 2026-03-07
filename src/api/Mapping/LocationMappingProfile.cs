using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Discovery;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Mapping;

/// <summary>
/// AutoMapper profile for mapping Location entities to DTOs.
/// Supports hierarchy properties for tree-based location management.
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
            .ForMember(dest => dest.ParentId, opt => opt.MapFrom(src => src.ParentId))
            .ForMember(dest => dest.Path, opt => opt.MapFrom(src => src.Path))
            .ForMember(dest => dest.Depth, opt => opt.MapFrom(src => src.Depth))
            .ForMember(dest => dest.SortOrder, opt => opt.MapFrom(src => src.SortOrder))
            .ForMember(dest => dest.TotalPrinterCount, opt => opt.MapFrom(src => src.TotalPrinterCount))
            .ForMember(dest => dest.Children, opt => opt.Ignore())
            .ForMember(dest => dest.CreatedAt, opt => opt.MapFrom(src => src.CreatedAt))
            .ForMember(dest => dest.ModifiedAt, opt => opt.MapFrom(src => src.ModifiedAt))
            .ForMember(dest => dest.IsActive, opt => opt.MapFrom(src => src.IsActive));

        // Location entity to LocationDetailsDto
        CreateMap<Location, LocationDetailsDto>()
            .ForMember(dest => dest.Id, opt => opt.MapFrom(src => src.Id))
            .ForMember(dest => dest.Name, opt => opt.MapFrom(src => src.Name))
            .ForMember(dest => dest.Description, opt => opt.MapFrom(src => src.Description))
            .ForMember(dest => dest.PrinterCount, opt => opt.MapFrom(src => src.Printers.Count))
            .ForMember(dest => dest.ParentId, opt => opt.MapFrom(src => src.ParentId))
            .ForMember(dest => dest.Path, opt => opt.MapFrom(src => src.Path))
            .ForMember(dest => dest.Depth, opt => opt.MapFrom(src => src.Depth))
            .ForMember(dest => dest.SortOrder, opt => opt.MapFrom(src => src.SortOrder))
            .ForMember(dest => dest.TotalPrinterCount, opt => opt.MapFrom(src => src.TotalPrinterCount))
            .ForMember(dest => dest.Children, opt => opt.Ignore())
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
