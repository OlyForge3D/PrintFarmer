import { useState } from 'react';
import clsx from 'clsx';
import { ShieldIcon } from '@/common/components/icons/MdiIcons';
import { Button } from '@/common/components/ui';
import type { FailureDetectionEvent, FailureDetectionPrinterStatusDto } from '@/types/api';
import {
  getFailureDetectionDisplayState,
  getFailureDetectionStateLabel,
} from '@/features/printers/utils/failureDetectionStatus';
import { FailureDetectionStatusModal } from '@/features/printers/components/FailureDetectionStatusModal';

interface FailureDetectionMonitoringBadgeProps {
  enabled: boolean;
  status?: FailureDetectionPrinterStatusDto;
  /** Live printer isPrinting from the printer card (state === 'Printing'). Overrides the
   *  potentially-stale isPrinting field inside the polled FailureDetection DTO. */
  isPrinting?: boolean;
  className?: string;
  printerId?: string;
  printerName?: string;
  recentEvents?: FailureDetectionEvent[];
}

export function FailureDetectionMonitoringBadge({
  enabled,
  status,
  isPrinting,
  className,
  printerId,
  printerName,
  recentEvents = [],
}: FailureDetectionMonitoringBadgeProps) {
  const [isDetailsOpen, setIsDetailsOpen] = useState(false);

  if (!enabled && !status) {
    return null;
  }

  // The DTO's isPrinting is computed by a 30-second backend poll and can lag behind the
  // live printer status (updated via SignalR). Override it with the live value when provided.
  // Only patch the reason for 'idle' state — that is the stale "not printing" case.
  // 'disabled' means the feature is intentionally off or the backend doesn't support it;
  // its reason is always authoritative and must never be overwritten.
  const stalePrintingMismatch = isPrinting === true && !!status && !status.isPrinting
    && status.state === 'idle';
  const effectiveStatus = isPrinting !== undefined && status
    ? {
        ...status,
        isPrinting,
        reason: stalePrintingMismatch
          ? 'Waiting for the monitoring service to begin scanning the current print.'
          : status.reason,
      }
    : status;

  const displayState = getFailureDetectionDisplayState(effectiveStatus);
  const label = getFailureDetectionStateLabel(displayState, enabled);
  const resolvedPrinterId = effectiveStatus?.printerId ?? printerId;
  const resolvedPrinterName = effectiveStatus?.printerName ?? printerName;

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
        status={effectiveStatus}
        printerId={resolvedPrinterId}
        printerName={resolvedPrinterName}
        recentEvents={recentEvents}
      />
    </>
  );
}
