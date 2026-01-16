using System;

namespace Farm.Infrastructure.Domain
{
    public class PrintApproval
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid PrintJobId { get; set; }
        public Guid? PrinterId { get; set; }
        public string? RequestedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
