using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Data;

public class AppSettingsEntity : IRevisionedEntity
{
    [Key]
    public int Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string SettingsJson { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Opaque compatibility token derived from <see cref="Revision"/>.
    /// </summary>
    [NotMapped]
    public byte[] RowVersion
    {
        get => Revision > 0 ? RevisionETag.EncodeBytes(Revision) : [];
        set => Revision = RevisionETag.Decode(value);
    }

    /// <inheritdoc/>
    public long Revision { get; set; } = 1;
}
