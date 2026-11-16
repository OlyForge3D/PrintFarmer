import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';
import { PrinterBackend, type PrinterMovementSupportStatus } from '@/types/api';

/** Guided-mode motion help. Mode is chosen only in User Settings; Expert shows no help. */
export function PrinterMotionHelp({
  movementSupportStatus,
  movementUnavailable = false,
  backend,
}: {
  movementSupportStatus?: PrinterMovementSupportStatus;
  movementUnavailable?: boolean;
  backend?: PrinterBackend;
}) {
  const { mode } = usePrinterControlsMode();
  let supportStatus = movementSupportStatus;
  if (supportStatus === undefined && movementUnavailable) {
    supportStatus = backend === PrinterBackend.PrusaLink ? 'Unknown' : 'Unsupported';
  }
  if (supportStatus === 'Unknown') return (
    <p role="status" className="mt-2 text-xs text-pf-text-secondary">
      PrintFarmer could not confirm remote Home and jog support. Check the printer connection and credentials, then reload this page to check again. Use the printer&apos;s physical controls while remote motion is unavailable.
    </p>
  );
  if (supportStatus === 'Unsupported') return (
    <p role="status" className="mt-2 text-xs text-pf-text-secondary">
      Remote Home and jog controls are not supported by this printer&apos;s API. Use the printer&apos;s physical controls.
    </p>
  );
  if (mode !== 'guided') return null;
  return (
    <div className="mt-2 space-y-1 text-xs text-pf-text-secondary">
      <p>Jog moves by the selected step in mm. Home finds the axis reference. Keep the motion area clear; check the printer before repeating an uncertain move.</p>
    </div>
  );
}
