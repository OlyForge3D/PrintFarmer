using System;
using System.ComponentModel.DataAnnotations;

namespace Farm.Infrastructure.Data;

public class AppSettingsEntity
{
    [Key]
    public int Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Optimistic concurrency token. Prevents silent overwrites from concurrent writers.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];
}
