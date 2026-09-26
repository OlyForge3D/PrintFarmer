using Farm.Infrastructure.Domain;

namespace Farm.Infrastructure.Services.Printers;

/// <summary>Backend-owned camera defaults and snapshot transport selection for a printer model.</summary>
public interface ISupportsPrinterCameraProfile
{
    /// <summary>Returns known model-specific URLs when configured camera discovery has no result.</summary>
    (string? StreamUrl, string? SnapshotUrl) GetDefaultCameraUrls(Printer printer);

    /// <summary>Whether this model and selected URL require the backend's triggered snapshot transport.</summary>
    bool ShouldTriggerSnapshot(Printer printer, string? snapshotUrl);
}
