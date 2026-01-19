using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;
using Farm.Infrastructure;
using Farm.Infrastructure.Annotations;

namespace Farm.Infrastructure.Domain;

// Mapping table linking harvest files to the gcode files created from them
// Preserves harvest metadata (slicer, material, nozzle, etc) separate from the library file
public class HarvestFileGcodeFileMapping
{
    public Guid Id { get; set; }

    public Guid HarvestDiscoveredFileId { get; set; }

    public HarvestDiscoveredFile HarvestDiscoveredFile { get; set; } = null!;

    public Guid GcodeFileId { get; set; }

    public GcodeFile GcodeFile { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
