using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Web.Api.Data.Entities
{
    public class PrintApproval
    {
        [Key]
        public Guid Id { get; set; }

        public Guid PrintJobId { get; set; }

        public Guid? PrinterId { get; set; }

        public string? RequestedBy { get; set; }

        public DateTimeOffset CreatedAt { get; set; }
    }
}
