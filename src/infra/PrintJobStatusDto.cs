using System;

namespace Farm.Infrastructure
{
    /// <summary>
    /// DTO for reporting print job status and all available properties.
    /// </summary>
    public class PrintJobStatusDto
    {
        public string? State { get; set; }
        public double? Progress { get; set; }
        public string? JobName { get; set; }
        public string? ThumbnailUrl { get; set; }
        public string? Error { get; set; }
    }
}
