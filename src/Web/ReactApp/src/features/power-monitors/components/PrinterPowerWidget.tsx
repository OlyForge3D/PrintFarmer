import { Tooltip } from '@/common/components/ui/Tooltip';

interface PrinterPowerWidgetProps {
  /** Live watts reading from smart plug (null if no smart plug connected). */
  liveWatts?: number | null;
  /** Static wattage value from Printer.wattage or model default. */
  staticWattage?: number | null;
  /** Hours the printer has been running in the current session (for kWh estimation). */
  runHours?: number;
}

/**
 * Displays power consumption in printer detail:
 * - Smart plug connected → live watts reading
 * - No smart plug, Printer.Wattage set → "~X kWh (estimated)" with tooltip
 * - No data → "—"
 */
export function PrinterPowerWidget({
  liveWatts,
  staticWattage,
  runHours = 0,
}: PrinterPowerWidgetProps) {
  // Live reading from smart plug
  if (liveWatts != null) {
    return (
      <div className="flex items-center gap-1.5 text-sm">
        <span className="inline-block w-2 h-2 rounded-full bg-pf-success animate-pulse" />
        <span className="font-medium text-pf-text-primary">{liveWatts}W</span>
        <span className="text-pf-text-tertiary">live</span>
      </div>
    );
  }

  // Estimation from static wattage field
  if (staticWattage != null && staticWattage > 0) {
    const estimatedKwh = ((staticWattage * runHours) / 1000).toFixed(2);
    return (
      <Tooltip
        content={`Estimated from printer wattage (${staticWattage}W × ${runHours.toFixed(1)}h). Connect a smart plug for real readings.`}
      >
        <div className="flex items-center gap-1.5 text-sm cursor-help">
          <span className="text-pf-text-secondary">~{estimatedKwh} kWh</span>
          <span className="text-pf-text-tertiary">(estimated)</span>
        </div>
      </Tooltip>
    );
  }

  // No data
  return (
    <span className="text-sm text-pf-text-tertiary" aria-label="No power data available">
      —
    </span>
  );
}
