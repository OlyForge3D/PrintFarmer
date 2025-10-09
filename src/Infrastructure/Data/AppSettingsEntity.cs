using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Data
{
    public class AppSettingsEntity
    {
        [Key]
        public int Id { get; set; }
        public string Key { get; set; } = string.Empty;
        public string SettingsJson { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
    }
}

