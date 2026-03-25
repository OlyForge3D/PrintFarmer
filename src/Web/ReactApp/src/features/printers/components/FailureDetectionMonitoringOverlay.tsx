import { useState } from 'react';
import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import type { FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionDisplayState,
  getFailureDetectionStateLabel,
} from '@/features/printers/utils/failureDetectionStatus';
import { FailureDetectionStatusModal } from '@/features/printers/components/FailureDetectionStatusModal';

interface FailureDetectionMonitoringOverlayProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
  printerName?: string;
}

function getChipStyles(state?: string): { border: string; glow: string; icon: string } {
  switch (state) {
    case 'monitoring':
      return {
        border: 'border-pf-success/40',
        glow: 'shadow-[0_2px_8px_rgba(34,197,94,0.4)]',
        icon: 'text-pf-success',
      };
    case 'idle':
      return {
        border: 'border-pf-accent/30',
        glow: 'shadow-[0_2px_8px_rgba(59,130,246,0.35)]',
        icon: 'text-pf-accent',
      };
    case 'misconfigured':
      return {
        border: 'border-pf-warning/40',
        glow: 'shadow-[0_2px_8px_rgba(245,158,11,0.4)]',
        icon: 'text-pf-warning',
      };
    case 'error':
      return {
        border: 'border-pf-error/40',
        glow: 'shadow-[0_2px_8px_rgba(239,68,68,0.4)]',
        icon: 'text-pf-error',
      };
    default:
      return {
        border: 'border-white/20',
        glow: '',
        icon: 'text-white/60',
      };
  }
}

export function FailureDetectionMonitoringOverlay({
  enabled,
  status,
  className,
  printerName,
}: FailureDetectionMonitoringOverlayProps) {
  const [isDetailsOpen, setIsDetailsOpen] = useState(false);

  if (!enabled && !status) {
    return null;
  }

  const displayState = getFailureDetectionDisplayState(status);
  const label = getFailureDetectionStateLabel(displayState, enabled);
  const styles = getChipStyles(displayState);
  const resolvedPrinterName = status?.printerName ?? printerName;

  return (
    <>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsDetailsOpen(true)}
        className={clsx(
          'inline-flex items-center gap-1.5 rounded-full border bg-slate-950/80 px-2.5 py-1 backdrop-blur-sm pointer-events-auto transition-colors hover:bg-slate-950/90 focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-white/70',
          styles.border,
          styles.glow,
          className
        )}
        aria-label={`Open spaghetti detection details${resolvedPrinterName ? ` for ${resolvedPrinterName}` : ''}`}
        aria-haspopup="dialog"
        aria-expanded={isDetailsOpen}
        title={`${label} • open spaghetti detection details`}
        iconLeft={<ShieldIcon className={clsx('h-3.5 w-3.5', styles.icon)} ariaLabel="Spaghetti detection" />}
      >
        {label}
      </Button>
      <FailureDetectionStatusModal
        isOpen={isDetailsOpen}
        onClose={() => setIsDetailsOpen(false)}
        enabled={enabled}
        status={status}
        printerName={resolvedPrinterName}
      />
    </>
  );
}
