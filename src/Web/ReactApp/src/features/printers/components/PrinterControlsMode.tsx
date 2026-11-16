import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';

/** Guided-mode motion help. Mode is chosen only in User Settings; Expert shows no help. */
export function PrinterMotionHelp({ movementUnavailable = false }: { movementUnavailable?: boolean }) {
  const { mode } = usePrinterControlsMode();
  if (movementUnavailable) return (
    <p role="status" className="mt-2 text-xs text-pf-text-secondary">
      Remote Home and jog controls are unavailable on this printer&apos;s API. Use the printer&apos;s physical controls.
    </p>
  );
  if (mode !== 'guided') return null;
  return (
    <div className="mt-2 space-y-1 text-xs text-pf-text-secondary">
      <p>Jog moves by the selected step in mm. Home finds the axis reference. Keep the motion area clear; check the printer before repeating an uncertain move.</p>
    </div>
  );
}
