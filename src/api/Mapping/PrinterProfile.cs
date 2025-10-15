using System;
using AutoMapper;
using Farm.Infrastructure.Domain;
using Farm.Web.Shared;

namespace Farm.Web.Api.Mapping
{
    public class PrinterProfile : Profile
    {
        public PrinterProfile()
        {
            CreateMap<Printer, PrinterBasicDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend));

            CreateMap<Printer, PrinterFastDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend));

            CreateMap<Printer, PrinterDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend))
                .ForMember(d => d.ManufacturerName, opt => opt.MapFrom(s => s.Manufacturer != null ? s.Manufacturer.Name : null))
                .ForMember(d => d.ModelName, opt => opt.MapFrom(s => s.Model != null ? s.Model.Name : null));

            CreateMap<PrinterCapabilities, PrinterCapabilitiesDto>();
        }
    }
}
