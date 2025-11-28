using System;
using AutoMapper;
using Farm.Infrastructure;
using Farm.Infrastructure.Domain;

namespace Farm.Web.Api.Mapping
{
    public class PrinterProfile : Profile
    {
        public PrinterProfile()
        {
            _ = CreateMap<Printer, PrinterBasicDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend));

            _ = CreateMap<Printer, PrinterFastDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend));

            _ = CreateMap<Printer, PrinterDto>()
                .ForMember(d => d.Backend, opt => opt.MapFrom(s => (PrinterBackend)s.Backend))
                .ForMember(d => d.ManufacturerName, opt => opt.MapFrom(s => s.Manufacturer != null ? s.Manufacturer.Name : null))
                .ForMember(d => d.ModelName, opt => opt.MapFrom(s => s.Model != null ? s.Model.Name : null));

            _ = CreateMap<PrinterCapabilities, PrinterCapabilitiesDto>();
        }
    }
}
