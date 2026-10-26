using Farm.Infrastructure.Domain;
using Microsoft.EntityFrameworkCore;

namespace Farm.Infrastructure.Services.Queue.Dispatch;

/// <summary>Reuses dispatch hard requirements without creating a temporary runnable job.</summary>
public interface IPrinterFileTargetValidator
{
    Task<bool> CanQueueAsync(GcodeFile file, Guid printerId, CancellationToken ct);
}

public partial class DispatchScorer
{
    public async Task<bool> CanQueueAsync(GcodeFile file, Guid printerId, CancellationToken ct)
    {
        Printer? printer = (await LoadPrintersAsync(printerId, ct)).SingleOrDefault();
        if (printer is null)
        {
            return false;
        }

        FilamentType? filament = string.IsNullOrWhiteSpace(file.RequiredMaterial) ? null
            : await db.FilamentTypes.AsNoTracking()
                .FirstOrDefaultAsync(x => x.Name == file.RequiredMaterial && x.IsActive, ct);
        HashSet<string> clusterMates = string.IsNullOrWhiteSpace(file.RequiredMaterial) ? []
            : await GetClusterMateNamesAsync(file.RequiredMaterial, ct);
        PrintJob candidate = new()
        {
            GcodeFileId = file.Id,
            GcodeFile = file,
            RequiredMaterialType = file.RequiredMaterial,
            RequiredNozzleDiameter = (decimal?)file.RequiredNozzleDiameter,
        };
        string? requiredModel = file.PrinterModel?.Name ?? file.ExtractedPrinterModelName;
        if ((file.PrinterModelId.HasValue && file.PrinterModelId != printer.ModelId) ||
            (!file.PrinterModelId.HasValue && !string.IsNullOrWhiteSpace(requiredModel) &&
             !string.Equals(requiredModel, printer.Model?.Name, StringComparison.OrdinalIgnoreCase) &&
             printer.Model?.Aliases.Any(alias =>
                 string.Equals(alias.SlicerModelName, requiredModel, StringComparison.OrdinalIgnoreCase)) != true) ||
            (!string.IsNullOrWhiteSpace(file.FirmwareFamily) &&
             !string.Equals(file.FirmwareFamily, printer.FirmwareFamily.ToString(), StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(file.GcodeDialect) &&
             !string.Equals(file.GcodeDialect, printer.GcodeDialect.ToString(), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (DispatchSafetyGates.EvaluateHardware(candidate, printer) is not null)
        {
            return false;
        }

        DispatchScore score = ScorePrinter(candidate, printer, filament, [], clusterMates,
            multiSlotEnabled: false, gramsAware: false, gramsContexts: null);

        // Availability/Ready is checked again at dispatch; a busy printer may accept waiting work.
        return !score.ScoreBreakdown.Any(factor => factor.Key != "Availability" &&
            factor.Value is { IsHardRequirement: true, Score: 0 }) &&
            ScoreBuildVolume(printer, file).Score > 0;
    }
}
