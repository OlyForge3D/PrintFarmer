import { Button } from '@/common/components/ui';
import { usePrinterControlsMode } from '@/features/printers/hooks/use-printer-controls-mode';

export function PrinterControlsMode() {
  const { mode, setMode, saveError, loadError, pending, loading, canSave, reloading, reload } = usePrinterControlsMode();
  return (
    <div className="mb-2 space-y-1">
      <div role="group" aria-label="Printer controls mode" className="flex flex-wrap items-center gap-1">
        <span className="mr-1 text-xs text-pf-text-secondary">Controls</span>
        <Button variant="toggle" size="sm" disabled={!canSave} aria-pressed={mode === 'guided'} onClick={() => setMode('guided')}>Guided</Button>
        <Button variant="toggle" size="sm" disabled={!canSave} aria-pressed={mode === 'expert'} onClick={() => setMode('expert')}>Expert</Button>
      </div>
      {(loading || pending) && <p role="status" className="text-xs text-pf-text-secondary">{pending ? 'Saving control mode to your account…' : 'Loading account preference; Guided shown for now.'}</p>}
      {(saveError || loadError) && (
        <div className="space-y-1">
          <p role="status" className="text-xs text-pf-warning">{saveError ?? loadError}</p>
          <Button variant="secondary" size="sm" disabled={reloading || pending} onClick={reload}>Reload preferences</Button>
        </div>
      )}
    </div>
  );
}

export function PrinterMotionHelp({ absolute }: { absolute: boolean }) {
  const { mode } = usePrinterControlsMode();
  const help = (
    <div className="space-y-1 text-xs text-pf-text-secondary">
      <p>Jog moves by the selected step in mm. Home finds the printer’s axis reference. Keep the motion area clear.</p>
      <p>{absolute ? 'GO moves to the absolute X, Y, and Z targets you enter (mm). Bracketed positions are readouts, not targets.' : 'GO moves by the entered amounts (mm); blank axes are left unchanged.'}</p>
    </div>
  );
  return mode === 'guided' ? <div className="mt-2">{help}</div> : (
    <details className="mt-2 text-xs text-pf-text-secondary">
      <summary className="w-fit cursor-pointer rounded-xs py-1 focus-visible:outline-2 focus-visible:outline-pf-accent">Motion help</summary>
      {help}
    </details>
  );
}
