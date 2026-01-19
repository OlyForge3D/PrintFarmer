namespace Farm.Infrastructure.Domain;

public enum HarvestErrorPhase
{
    Discovery = 0,    // Failed during file listing
    Download = 1,     // Failed during file download
    Processing = 2,   // Failed during file processing/import
    Completion = 3    // Failed during finalization
}
