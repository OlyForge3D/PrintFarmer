import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';

/** Guided-mode motion help. Mode is chosen only in User Settings; Expert shows no help. */
export function PrinterMotionHelp({ absolute }: { absolute: boolean }) {
  const { mode } = usePrinterControlsMode();
  if (mode !== 'guided') return null;
  return (
    <div className="mt-2 space-y-1 text-xs text-pf-text-secondary">
      <p>Jog moves by the selected step in mm. Home finds the printer’s axis reference. Keep the motion area clear.</p>
      <p>{absolute ? 'GO moves to the absolute X, Y, and Z targets you enter (mm). Bracketed positions are readouts, not targets.' : 'GO moves by the entered amounts (mm); blank axes are left unchanged.'}</p>
    </div>
  );
}
