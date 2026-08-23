import { useState } from 'react';
import clsx from 'clsx';
import { RulerIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import { useCalibrationCandidateFromFleet } from '@/features/printers/hooks/useCalibrationCandidatesFleet';
import { getCalibrationSetupStage } from '@/features/printers/utils/calibrationSetupStage';
import { CalibrationSetupModal } from '@/features/printers/components/CalibrationSetupModal';

interface CalibrationSetupPromptProps {
  printerId: string;
  printerName: string;
  rowVersion?: string | null;
  className?: string;
}

const STAGE_COPY = {
  'not-started': {
    label: 'Set up calibration',
    title: 'Calibration hasn\u2019t been set up yet \u2014 click to get started.',
  },
  partial: {
    label: 'Finish calibration setup',
    title: 'Calibration setup is partially complete \u2014 click to finish it.',
  },
} as const;

// Deliberately blue/amber "invitation" tones, not red/error — this is an
// onboarding nudge (issue #1923 AC "reads as an onboarding prompt, not an
// error"), distinct per stage so "never set up" and "partially set up" read
// differently at a glance without opening the printer's details.
const STAGE_CLASSES = {
  'not-started': 'bg-blue-500/20 border-blue-400/40 text-blue-300 hover:bg-blue-500/30',
  partial: 'bg-amber-500/20 border-amber-400/40 text-amber-300 hover:bg-amber-500/30',
} as const;

/**
 * Self-contained onboarding affordance for a printer whose calibration setup
 * is incomplete (issue #1923). Mirrors `FailureDetectionMonitoringBadge`'s
 * pattern: it owns its own fetch (via the fleet hook, so N+1 card fetches
 * never happen) and its own modal-open state, so no new props need to be
 * threaded through `CompactPrinterCard`/`DetailedPrinterCard`/`PrinterTableView`.
 * Renders nothing once the printer is eligible or before fleet data resolves.
 */
export function CalibrationSetupPrompt({
  printerId,
  printerName,
  rowVersion,
  className,
}: CalibrationSetupPromptProps) {
  const [isSetupOpen, setIsSetupOpen] = useState(false);
  const { data: candidate } = useCalibrationCandidateFromFleet(printerId);
  const stage = candidate ? getCalibrationSetupStage(candidate) : undefined;

  if (!stage) {
    return null;
  }

  const copy = STAGE_COPY[stage];

  return (
    <>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsSetupOpen(true)}
        title={copy.title}
        aria-label={`${copy.label} for ${printerName}`}
        aria-haspopup="dialog"
        aria-expanded={isSetupOpen}
        className={clsx(
          'inline-flex items-center gap-1 px-2 py-0.5 rounded-xs text-xs font-medium border shrink-0 transition-colors',
          'focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-2',
          STAGE_CLASSES[stage],
          className,
        )}
      >
        {/* The Button's aria-label already names the printer + action, so the
            icon is purely decorative here — hide it from the accessible name. */}
        <span aria-hidden="true">
          <RulerIcon className="w-3 h-3" />
        </span>
        {copy.label}
      </Button>
      <CalibrationSetupModal
        isOpen={isSetupOpen}
        onClose={() => setIsSetupOpen(false)}
        printerId={printerId}
        printerName={printerName}
        rowVersion={rowVersion}
      />
    </>
  );
}
