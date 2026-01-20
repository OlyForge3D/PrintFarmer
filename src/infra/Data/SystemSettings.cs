using System;

namespace Farm.Infrastructure.Data;

public class SystemSettings
{
    public int Id { get; set; }

    public string SettingsJson { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
