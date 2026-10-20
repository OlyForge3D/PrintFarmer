import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';

/** Guided-mode motion help. Mode is chosen only in User Settings; Expert shows no help. */
export function PrinterMotionHelp() {
  const { mode } = usePrinterControlsMode();
  if (mode !== 'guided') return null;
  return (
    <div className="mt-2 space-y-1 text-xs text-pf-text-secondary">
      <p>Jog moves by the selected step in mm. Home finds the axis reference. Keep the motion area clear; check the printer before repeating an uncertain move.</p>
    </div>
  );
}
