namespace Farm.Slicer.Module.Contracts;

/// <summary>Source-qualified profile identity; user profiles require their stable database ID.</summary>
public sealed class SliceProfileSelection
{
    public string Source { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public Guid? CustomProfileId { get; set; }
}

/// <summary>Complete profile selection, with filaments ordered by physical extruder index.</summary>
public sealed class SliceProfileSelections
{
    public SliceProfileSelection? Machine { get; set; }

    public SliceProfileSelection? Process { get; set; }

    public List<SliceProfileSelection> Filaments { get; set; } = [];
}
