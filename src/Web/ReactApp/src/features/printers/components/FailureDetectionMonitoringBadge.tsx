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

interface FailureDetectionMonitoringBadgeProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  className?: string;
  printerName?: string;
}

export function FailureDetectionMonitoringBadge({
  enabled,
  status,
  className,
  printerName,
}: FailureDetectionMonitoringBadgeProps) {
  const [isDetailsOpen, setIsDetailsOpen] = useState(false);

  if (!enabled && !status) {
    return null;
  }

  const displayState = getFailureDetectionDisplayState(status);
  const label = getFailureDetectionStateLabel(displayState, enabled);
  const resolvedPrinterName = status?.printerName ?? printerName;

  // Color mapping based on state
  const iconColorClass = {
    monitoring: 'text-pf-success',
    checking: 'text-pf-text-secondary',
    disabled: 'text-pf-text-tertiary',
    error: 'text-pf-error',
  }[displayState] || 'text-pf-text-secondary';

  return (
    <>
      <Button
        type="button"
        variant="unstyled"
        onClick={() => setIsDetailsOpen(true)}
        className={clsx(
          'p-1 rounded-full transition-colors',
          'focus-visible:outline-hidden focus-visible:ring-2 focus-visible:ring-pf-accent focus-visible:ring-offset-2',
          'hover:bg-white/10',
          className
        )}
        aria-label={`Open spaghetti detection details${resolvedPrinterName ? ` for ${resolvedPrinterName}` : ''}`}
        aria-haspopup="dialog"
        aria-expanded={isDetailsOpen}
        title={`${label} • click for details`}
      >
        <ShieldIcon
          className={clsx('h-4 w-4', iconColorClass)}
          ariaLabel={`Failure detection ${label.toLowerCase()}`}
        />
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
