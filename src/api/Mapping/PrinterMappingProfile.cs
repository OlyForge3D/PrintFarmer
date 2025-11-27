using AutoMapper;
using Farm.Web.Api.Services;
using Farm.Web.Shared;
using Farm.Web.Shared.Contracts.Printers.Moonraker;
using SharedModels = Farm.Web.Shared;

namespace Farm.Web.Api.Mapping
{
    public class PrinterMappingProfile : Profile
    {
        public PrinterMappingProfile()
        {
            // Moonraker service models -> Shared DTOs
            _ = CreateMap<HistoryJob, SharedModels.HistoryJob>()
                .ForMember(dest => dest.JobId, opt => opt.MapFrom(src => src.JobId))
                .ForMember(dest => dest.Exists, opt => opt.MapFrom(src => src.Exists))
                .ForMember(dest => dest.Filename, opt => opt.MapFrom(src => src.Filename))
                .ForMember(dest => dest.User, opt => opt.MapFrom(src => src.User))
                .ForMember(dest => dest.Metadata, opt => opt.MapFrom(src => src.Metadata))
                .ForMember(dest => dest.PrintDuration, opt => opt.MapFrom(src => src.GetPrintDuration()))
                .ForMember(dest => dest.TotalDuration, opt => opt.MapFrom(src => src.GetTotalDuration()))
                .ForMember(dest => dest.StartTime, opt => opt.MapFrom(src => src.GetStartTimeAsDateTime()))
                .ForMember(dest => dest.EndTime, opt => opt.MapFrom(src => src.GetEndTimeAsDateTime()))
                .ForMember(dest => dest.Status, opt => opt.MapFrom(src => src.Status))
                .ForMember(dest => dest.FilamentUsed, opt => opt.MapFrom(src => src.FilamentUsed))
                .ForMember(dest => dest.AuxiliaryData, opt => opt.MapFrom(src => src.AuxiliaryData));

            _ = CreateMap<AuxiliaryData, SharedModels.AuxiliaryData>();

            _ = CreateMap<HistoryTotals, SharedModels.HistoryTotals>()
                .ForMember(dest => dest.JobTotals, opt => opt.MapFrom(src => src.JobTotals))
                .ForMember(dest => dest.AuxiliaryTotals, opt => opt.MapFrom(src => src.AuxiliaryTotals));

            _ = CreateMap<JobTotals, SharedModels.JobTotals>()
                .ForMember(dest => dest.TotalJobs, opt => opt.MapFrom(src => src.TotalJobs))
                .ForMember(dest => dest.TotalTime, opt => opt.MapFrom(src => src.TotalTime))
                .ForMember(dest => dest.TotalPrintTime, opt => opt.MapFrom(src => src.TotalPrintTime))
                .ForMember(dest => dest.TotalFilamentUsed, opt => opt.MapFrom(src => src.TotalFilamentUsed))
                .ForMember(dest => dest.LongestJob, opt => opt.MapFrom(src => src.LongestJob))
                .ForMember(dest => dest.LongestPrint, opt => opt.MapFrom(src => src.LongestPrint));

            _ = CreateMap<AuxiliaryTotals, SharedModels.AuxiliaryTotals>();
        }
    }
}
