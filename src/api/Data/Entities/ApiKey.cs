using System;

namespace Farm.Web.Api.Data.Entities
{
    public class ApiKey
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid? UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        // NOTE: In production, store a hash instead of plaintext
        public string KeyHash { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ExpiresAt { get; set; }
    }
}
